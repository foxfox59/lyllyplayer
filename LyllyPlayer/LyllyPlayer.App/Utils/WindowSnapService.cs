using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using LyllyPlayer.Utils;

namespace LyllyPlayer.Utils;

/// <summary>
/// Gapless snapping ("magnet latch + slide") between LyllyPlayer windows only.
/// Uses WM_MOVING to constrain the proposed window rect during interactive dragging.
/// </summary>
public static class WindowSnapService
{
    // Win32 move lifecycle
    private const int WM_ENTERSIZEMOVE = 0x0231;
    private const int WM_EXITSIZEMOVE = 0x0232;
    private const int WM_MOVING = 0x0216;

    // Thresholds (pixels). snapOut > snapIn gives the "tear off harder" feel.
    // Keep SnapIn modest to avoid "aggressive" snapping; direction gating provides the snap feel instead.
    private const int SnapInPx = 14;
    private const int SnapOutPx = 44;
    private const int AlignInPx = 10;
    private const int MinOverlapPx = 16;

    private enum SnapSide { None, Left, Right, Top, Bottom }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    private sealed class SnapWindow
    {
        public IntPtr Hwnd { get; }
        public WeakReference<Window> WindowRef { get; }
        public HwndSource? Source { get; set; }

        public bool IsDragging;
        public IntPtr LatchedTargetHwnd;
        public SnapSide LatchedSide = SnapSide.None;
        public int DragOffsetX;
        public int DragOffsetY;
        public bool HasLastRaw;
        public RECT LastRaw;
        public bool HasLastApplied;
        public RECT LastApplied;

        // When Main is being dragged, we capture cluster member offsets relative to Main at drag start.
        // This avoids drift / stale parent-child latch trees and ensures cluster movement is consistent from Main.
        public Dictionary<IntPtr, POINT>? ClusterOffsetsFromMainAtDragStart;

        public SnapWindow(IntPtr hwnd, Window w)
        {
            Hwnd = hwnd;
            WindowRef = new WeakReference<Window>(w);
        }
    }

    private static readonly ConcurrentDictionary<IntPtr, SnapWindow> _windows = new();
    private static volatile bool _enabled = true;

    public static void SetEnabled(bool enabled) => _enabled = enabled;

    public static bool IsEnabled => _enabled;

    public static bool AnyWindowDragging
    {
        get
        {
            try { return _windows.Values.Any(w => w.IsDragging); }
            catch { return false; }
        }
    }
    private sealed class LatchRelation
    {
        public IntPtr TargetHwnd { get; set; }
        public SnapSide Side { get; set; }
        // Offset along the snapped edge (pixels). For Left/Right latch: child.Top - target.Top.
        // For Top/Bottom latch: child.Left - target.Left.
        public int EdgeOffsetPx { get; set; }
    }

    // Persistent "snapped to" relationships (survive after drag ends) so clusters can move together.
    // Key = snapped window hwnd, Value = the target window it is latched to.
    private static readonly ConcurrentDictionary<IntPtr, LatchRelation> _latchedTo = new();
    private static volatile int _clusterMoveGuard;
    private const int RestoreAdjacencyPx = 2;

    public static void RestoreLatchedRelationsFromCurrentPositionsBestEffort()
    {
        try
        {
            // Don't mutate latch state while the user is actively dragging any window.
            if (AnyWindowDragging)
                return;

            var targets = GetSnapTargets(IntPtr.Zero);
            if (targets.Count <= 1)
                return;

            var main = targets.FirstOrDefault(t => t.isMain);
            var hasMain = main.hwnd != IntPtr.Zero;
            var mainRect = main.rect;

            foreach (var w in targets)
            {
                if (w.hwnd == IntPtr.Zero)
                    continue;
                if (w.isMain)
                    continue;
                if (_latchedTo.ContainsKey(w.hwnd))
                    continue;

                // Only record a relation if the window is already visually "gapless adjacent" to another window.
                if (TryInferLatchFromAdjacency(w.hwnd, w.rect, targets, hasMain ? mainRect : (RECT?)null,
                        out var targetHwnd, out var side))
                {
                    var edgeOffset = 0;
                    try
                    {
                        var tr = targets.FirstOrDefault(t => t.hwnd == targetHwnd).rect;
                        edgeOffset = ComputeEdgeOffset(w.rect, tr, side);
                    }
                    catch { /* ignore */ }
                    _latchedTo[w.hwnd] = new LatchRelation { TargetHwnd = targetHwnd, Side = side, EdgeOffsetPx = edgeOffset };
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// When window sizes change programmatically (e.g. UI scale), latched windows can remain at their old top-left.
    /// This re-applies the stored latch relations (best-effort) to keep clusters gapless.
    /// </summary>
    public static void SettleLatchedRelationsBestEffort()
    {
        try
        {
            if (AnyWindowDragging)
                return;
            if (_clusterMoveGuard != 0)
                return;

            // Prefer settling from Main if available (ensures cluster leader ordering).
            var main = _windows.Keys.FirstOrDefault(IsMainWindow);
            if (main != IntPtr.Zero && TryGetWindowRect(main, out var mr))
            {
                TryMoveLatchedCluster(rootTargetHwnd: main, rootRectOverride: mr);
                return;
            }

            // Fallback: apply each relation independently.
            foreach (var kv in _latchedTo.ToArray())
            {
                var child = kv.Key;
                var rel = kv.Value;
                if (child == IntPtr.Zero || rel.TargetHwnd == IntPtr.Zero)
                    continue;
                if (IsIconic(child) || !IsWindowVisible(child))
                    continue;
                if (!TryGetWindowRect(child, out var cr) || !TryGetWindowRect(rel.TargetHwnd, out var tr))
                    continue;
                if (!TryComputeLatchedRect(cr, tr, rel.Side, rel.EdgeOffsetPx, out var desired))
                    continue;

                if (desired.Left == cr.Left && desired.Top == cr.Top)
                    continue;

                SetWindowPos(child, IntPtr.Zero, desired.Left, desired.Top, 0, 0,
                    SWP_NOZORDER | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOSENDCHANGING | SWP_ASYNCWINDOWPOS);
            }
        }
        catch
        {
            // ignore
        }
    }

    public static void Register(Window w)
    {
        if (w is null)
            return;

        void TryRegisterNow()
        {
            try
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                if (hwnd == IntPtr.Zero)
                    return;

                var info = _windows.GetOrAdd(hwnd, _ => new SnapWindow(hwnd, w));
                var src = HwndSource.FromHwnd(hwnd);
                if (src is null)
                    return;

                // Hook only once per hwnd.
                if (!ReferenceEquals(info.Source, src))
                {
                    info.Source = src;
                    src.AddHook(WinHook);
                }

                w.Closed -= WindowOnClosed;
                w.Closed += WindowOnClosed;
            }
            catch { /* ignore */ }
        }

        if (w.IsLoaded)
        {
            TryRegisterNow();
            return;
        }

        w.SourceInitialized -= WindowOnSourceInitialized;
        w.SourceInitialized += WindowOnSourceInitialized;
        return;

        void WindowOnSourceInitialized(object? sender, EventArgs e) => TryRegisterNow();
        void WindowOnClosed(object? sender, EventArgs e)
        {
            try
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    _windows.TryRemove(hwnd, out _);
                    // Remove any latch relations involving this window.
                    _latchedTo.TryRemove(hwnd, out _);
                    foreach (var kv in _latchedTo.ToArray())
                    {
                        if (kv.Value.TargetHwnd == hwnd)
                            _latchedTo.TryRemove(kv.Key, out _);
                    }
                }
            }
            catch { /* ignore */ }
        }
    }

    private static IntPtr WinHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        try
        {
            if (!_windows.TryGetValue(hwnd, out var self))
                return IntPtr.Zero;

            if (msg == WM_ENTERSIZEMOVE)
            {
                self.IsDragging = true;
                // Keep latch target across tiny pauses; it's cleared on exit.
                try
                {
                    if (TryGetWindowRect(hwnd, out var wr) && GetCursorPos(out var p))
                    {
                        self.DragOffsetX = p.X - wr.Left;
                        self.DragOffsetY = p.Y - wr.Top;
                    }
                }
                catch { /* ignore */ }
                self.HasLastRaw = false;
                self.HasLastApplied = false;
                self.ClusterOffsetsFromMainAtDragStart = null;

                // If Main begins dragging and has a cluster, snapshot member offsets from Main immediately.
                try
                {
                    if (IsMainWindow(hwnd) && HasAnyLatchedChildren(hwnd) && TryGetWindowRect(hwnd, out var mainRect))
                    {
                        var members = GetClusterMembersIncludingRoot(hwnd);
                        if (members.Count > 1)
                        {
                            var map = new Dictionary<IntPtr, POINT>(members.Count);
                            foreach (var m in members)
                            {
                                if (m == hwnd) continue;
                                if (!TryGetWindowRect(m, out var r)) continue;
                                map[m] = new POINT { X = r.Left - mainRect.Left, Y = r.Top - mainRect.Top };
                            }
                            self.ClusterOffsetsFromMainAtDragStart = map;
                        }
                    }
                }
                catch { /* ignore */ }
                handled = false;
                return IntPtr.Zero;
            }

            if (msg == WM_EXITSIZEMOVE)
            {
                try
                {
                    // "Settle" after interactive move: enforce exact gapless coordinates once.
                    // This prevents small seams/drift caused by async window pos + rounding during heavy UI activity.
                    if (IsMainWindow(hwnd))
                    {
                        if (TryGetWindowRect(hwnd, out var mr))
                        {
                            var offsets = self.ClusterOffsetsFromMainAtDragStart;
                            if (offsets is not null && offsets.Count > 0)
                            {
                                System.Threading.Interlocked.Exchange(ref _clusterMoveGuard, 1);
                                try
                                {
                                    foreach (var kv in offsets)
                                    {
                                        var child = kv.Key;
                                        if (child == IntPtr.Zero) continue;
                                        if (IsIconic(child) || !IsWindowVisible(child)) continue;

                                        var desiredLeft = mr.Left + kv.Value.X;
                                        var desiredTop = mr.Top + kv.Value.Y;
                                        SetWindowPos(child, IntPtr.Zero, desiredLeft, desiredTop, 0, 0,
                                            SWP_NOZORDER | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOSENDCHANGING);
                                    }
                                }
                                finally
                                {
                                    System.Threading.Interlocked.Exchange(ref _clusterMoveGuard, 0);
                                }
                            }
                        }
                    }
                    else
                    {
                        if (_latchedTo.TryGetValue(hwnd, out var rel) &&
                            rel.TargetHwnd != IntPtr.Zero &&
                            TryGetWindowRect(hwnd, out var cr) &&
                            TryGetWindowRect(rel.TargetHwnd, out var tr) &&
                            TryComputeLatchedRect(cr, tr, rel.Side, rel.EdgeOffsetPx, out var desired))
                        {
                            SetWindowPos(hwnd, IntPtr.Zero, desired.Left, desired.Top, 0, 0,
                                SWP_NOZORDER | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOSENDCHANGING);
                            // Refresh stored offset from the settled final coordinates.
                            rel.EdgeOffsetPx = ComputeEdgeOffset(desired, tr, rel.Side);
                        }
                    }
                }
                catch { /* ignore */ }

                self.IsDragging = false;
                self.LatchedTargetHwnd = IntPtr.Zero;
                self.LatchedSide = SnapSide.None;
                self.HasLastRaw = false;
                self.HasLastApplied = false;
                self.ClusterOffsetsFromMainAtDragStart = null;
                handled = false;
                return IntPtr.Zero;
            }

            if (msg != WM_MOVING || lParam == IntPtr.Zero)
                return IntPtr.Zero;

            if (!self.IsDragging)
                return IntPtr.Zero;

            if (!_enabled)
                return IntPtr.Zero;

            // Proposed rect (screen pixels).
            var proposed = Marshal.PtrToStructure<RECT>(lParam);
            if (proposed.Width <= 0 || proposed.Height <= 0)
                return IntPtr.Zero;

            // Compute cursor-driven "raw" rect (what the user is trying to do).
            // This avoids the classic WM_MOVING trap where our previous override becomes the next "proposed",
            // making detaching feel impossible.
            var raw = proposed;
            try
            {
                if (GetCursorPos(out var p))
                {
                    // WM_MOVING rect size can be unreliable with borderless + WindowChrome + transparency.
                    // Prefer the real current window size from Win32 if available.
                    var w = proposed.Width;
                    var h = proposed.Height;
                    try
                    {
                        if (TryGetWindowRect(hwnd, out var curRect) && curRect.Width > 0 && curRect.Height > 0)
                        {
                            w = curRect.Width;
                            h = curRect.Height;
                        }
                    }
                    catch { /* ignore */ }

                    var left = p.X - self.DragOffsetX;
                    var top = p.Y - self.DragOffsetY;
                    raw.Left = left;
                    raw.Top = top;
                    raw.Right = left + w;
                    raw.Bottom = top + h;
                }
            }
            catch { /* ignore */ }

            // Gather viable targets: other registered windows that are visible and normal.
            var targets = GetSnapTargets(hwnd);
            if (targets.Count == 0)
                return IntPtr.Zero;

            // When dragging Main, don't snap against windows that are already in Main's own cluster.
            // Otherwise Main can "snap to thin air" due to children temporarily lagging during cluster movement.
            if (IsMainWindow(hwnd))
            {
                try
                {
                    targets = targets.Where(t => !IsInCluster(t.hwnd, rootLeaderHwnd: hwnd)).ToList();
                }
                catch { /* ignore */ }
                // If nothing outside the cluster exists, we still need cluster-follow to run.
                // An empty target list just means "no snapping targets this tick".
            }

            // If we are latched, enforce latch with snap-out hysteresis.
            if (self.LatchedSide != SnapSide.None && self.LatchedTargetHwnd != IntPtr.Zero)
            {
                if (TryGetWindowRect(self.LatchedTargetHwnd, out var targetRect))
                {
                    // Detach if pulled away far enough.
                    if (ShouldDetach(raw, targetRect, self.LatchedSide))
                    {
                        self.LatchedSide = SnapSide.None;
                        self.LatchedTargetHwnd = IntPtr.Zero;
                        _latchedTo.TryRemove(hwnd, out _);
                    }
                    else
                    {
                        // Still latched: apply the constraint (gapless touch + optional align magnets).
                        proposed = raw;
                        if (ApplyLatchedMove(ref proposed, targetRect, self.LatchedSide, allowAlignMagnets: !IsMainWindow(hwnd)))
                        {
                            // Persist the relation so clusters follow when the target is moved.
                            _latchedTo[hwnd] = new LatchRelation
                            {
                                TargetHwnd = self.LatchedTargetHwnd,
                                Side = self.LatchedSide,
                                EdgeOffsetPx = ComputeEdgeOffset(proposed, targetRect, self.LatchedSide)
                            };

                            // Move latched cluster based on the *final snapped rect* (not cursor raw).
                            TryMoveClusterIfMainLeader(self, hwnd, proposed);

                            Marshal.StructureToPtr(proposed, lParam, fDeleteOld: false);
                            handled = true;
                            self.LastRaw = raw;
                            self.HasLastRaw = true;
                            self.LastApplied = proposed;
                            self.HasLastApplied = true;
                            return IntPtr.Zero;
                        }
                    }
                }
                else
                {
                    self.LatchedSide = SnapSide.None;
                    self.LatchedTargetHwnd = IntPtr.Zero;
                    _latchedTo.TryRemove(hwnd, out _);
                }
            }

            // Not latched: find best latch candidate (min distance) and latch if within SnapInPx,
            // but only when we are moving *toward* that edge (direction gating).
            if (TryFindBestLatch(raw, self.HasLastRaw ? self.LastRaw : (RECT?)null, targets, out var latch))
            {
                self.LatchedTargetHwnd = latch.targetHwnd;
                self.LatchedSide = latch.side;

                // Apply the latch immediately.
                proposed = raw;
                if (ApplyLatchedMove(ref proposed, latch.targetRect, latch.side, allowAlignMagnets: !IsMainWindow(hwnd)))
                {
                    _latchedTo[hwnd] = new LatchRelation
                    {
                        TargetHwnd = latch.targetHwnd,
                        Side = latch.side,
                        EdgeOffsetPx = ComputeEdgeOffset(proposed, latch.targetRect, latch.side)
                    };
                    // If Main snaps onto an existing cluster, Main should become the leader.
                    // Convert "Main latched to X" into "X latched to Main" so the cluster sticks to Main.
                    if (IsMainWindow(hwnd))
                        PromoteMainToLeader(hwnd, latch.targetHwnd, latch.side);

                    TryMoveClusterIfMainLeader(self, hwnd, proposed);

                    Marshal.StructureToPtr(proposed, lParam, fDeleteOld: false);
                    handled = true;
                    return IntPtr.Zero;
                }
            }

            // Not snapped this tick: still move the cluster when dragging Main, using the raw rect.
            // (Main dragging does not always require handled=true.)
            TryMoveClusterIfMainLeader(self, hwnd, raw);

            self.LastRaw = raw;
            self.HasLastRaw = true;
            self.LastApplied = raw;
            self.HasLastApplied = true;
            return IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static void TryMoveClusterIfMainLeader(SnapWindow self, IntPtr hwnd, RECT finalRect)
    {
        try
        {
            if (_clusterMoveGuard != 0)
                return;
            if (!HasAnyLatchedChildren(hwnd))
                return;
            if (!IsMainWindow(hwnd))
                return;

            // Prefer the drag-start offsets snapshot if available; it's more stable than any evolving latch tree.
            var offsets = self.ClusterOffsetsFromMainAtDragStart;
            if (offsets is not null && offsets.Count > 0)
            {
                System.Threading.Interlocked.Exchange(ref _clusterMoveGuard, 1);
                try
                {
                    foreach (var kv in offsets)
                    {
                        var child = kv.Key;
                        if (child == IntPtr.Zero) continue;
                        if (IsIconic(child) || !IsWindowVisible(child)) continue;

                        var desiredLeft = finalRect.Left + kv.Value.X;
                        var desiredTop = finalRect.Top + kv.Value.Y;

                        if (TryGetWindowRect(child, out var cr) && cr.Left == desiredLeft && cr.Top == desiredTop)
                            continue;

                        SetWindowPos(child, IntPtr.Zero, desiredLeft, desiredTop, 0, 0,
                            SWP_NOZORDER | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOSENDCHANGING | SWP_ASYNCWINDOWPOS);
                    }
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref _clusterMoveGuard, 0);
                }
                return;
            }

            // Fallback: compute from latch relations.
            TryMoveLatchedCluster(hwnd, finalRect);
        }
        catch
        {
            // ignore
        }
    }

    private static HashSet<IntPtr> GetClusterMembersIncludingRoot(IntPtr rootLeaderHwnd)
    {
        var set = new HashSet<IntPtr>();
        try
        {
            if (rootLeaderHwnd == IntPtr.Zero)
                return set;
            set.Add(rootLeaderHwnd);

            // BFS by child edges (TargetHwnd == current).
            var q = new Queue<IntPtr>();
            q.Enqueue(rootLeaderHwnd);

            while (q.Count > 0)
            {
                var cur = q.Dequeue();
                foreach (var kv in _latchedTo.ToArray())
                {
                    if (kv.Value.TargetHwnd != cur)
                        continue;
                    var child = kv.Key;
                    if (child == IntPtr.Zero)
                        continue;
                    if (!set.Add(child))
                        continue;
                    q.Enqueue(child);
                }
            }
        }
        catch { /* ignore */ }
        return set;
    }

    private static List<(IntPtr hwnd, RECT rect, bool isMain)> GetSnapTargets(IntPtr excludeHwnd)
    {
        var list = new List<(IntPtr hwnd, RECT rect, bool isMain)>(8);
        foreach (var kv in _windows)
        {
            var hwnd = kv.Key;
            if (hwnd == IntPtr.Zero || (excludeHwnd != IntPtr.Zero && hwnd == excludeHwnd))
                continue;

            if (!TryGetWindowRect(hwnd, out var r))
                continue;
            if (r.Width <= 1 || r.Height <= 1)
                continue;

            // Best-effort: ignore minimized/invisible windows.
            if (!IsWindowVisible(hwnd))
                continue;
            if (IsIconic(hwnd))
                continue;

            var isMain = false;
            try
            {
                if (kv.Value.WindowRef.TryGetTarget(out var w) && w is not null)
                    isMain = w is LyllyPlayer.MainWindow;
            }
            catch { /* ignore */ }

            list.Add((hwnd, r, isMain));
        }
        return list;
    }

    private static bool TryInferLatchFromAdjacency(
        IntPtr movingHwnd,
        RECT moving,
        List<(IntPtr hwnd, RECT rect, bool isMain)> targets,
        RECT? mainRect,
        out IntPtr targetHwnd,
        out SnapSide side)
    {
        var targetHwndLocal = IntPtr.Zero;
        var sideLocal = SnapSide.None;
        var bestScore = int.MaxValue;

        foreach (var t in targets)
        {
            if (t.hwnd == IntPtr.Zero || t.hwnd == movingHwnd)
                continue;

            var tr = t.rect;
            if (tr.Width <= 1 || tr.Height <= 1)
                continue;

            var priority = ComputeTargetPriority(t.isMain, mainRect, tr);

            // moving is to the RIGHT of target (touching target.Right)
            var gapR = Math.Abs(moving.Left - tr.Right);
            if (gapR <= RestoreAdjacencyPx && HasVerticalOverlap(moving, tr))
                Consider(t.hwnd, SnapSide.Right, gapR, priority);

            // moving is to the LEFT of target (touching target.Left)
            var gapL = Math.Abs(moving.Right - tr.Left);
            if (gapL <= RestoreAdjacencyPx && HasVerticalOverlap(moving, tr))
                Consider(t.hwnd, SnapSide.Left, gapL, priority);

            // moving is BELOW target (touching target.Bottom)
            var gapB = Math.Abs(moving.Top - tr.Bottom);
            if (gapB <= RestoreAdjacencyPx && HasHorizontalOverlap(moving, tr))
                Consider(t.hwnd, SnapSide.Bottom, gapB, priority);

            // moving is ABOVE target (touching target.Top)
            var gapT = Math.Abs(moving.Bottom - tr.Top);
            if (gapT <= RestoreAdjacencyPx && HasHorizontalOverlap(moving, tr))
                Consider(t.hwnd, SnapSide.Top, gapT, priority);
        }

        targetHwnd = targetHwndLocal;
        side = sideLocal;
        return sideLocal != SnapSide.None && targetHwndLocal != IntPtr.Zero;

        void Consider(IntPtr hwnd, SnapSide s, int gap, int priority)
        {
            // Strongly prefer smaller gap; then prefer Main/cluster.
            var score = (gap * 100) + (priority * 10);
            if (score >= bestScore)
                return;
            bestScore = score;
            targetHwndLocal = hwnd;
            sideLocal = s;
        }
    }

    private static bool TryFindBestLatch(RECT moving, RECT? lastRaw, List<(IntPtr hwnd, RECT rect, bool isMain)> targets, out (IntPtr targetHwnd, RECT targetRect, SnapSide side, int dist) best)
    {
        var bestLocal = default((IntPtr targetHwnd, RECT targetRect, SnapSide side, int dist));
        bestLocal.dist = int.MaxValue;
        bestLocal.side = SnapSide.None;
        var bestScore = int.MaxValue;

        // Prefer snapping to MainWindow when multiple candidates are plausible.
        // If a non-main window is already docked to main (cluster), treat it as "second best".
        var hasMain = targets.Any(t => t.isMain);
        RECT mainRect = default;
        if (hasMain)
        {
            var mr = targets.First(t => t.isMain).rect;
            mainRect = mr;
        }

        foreach (var t in targets)
        {
            var tr = t.rect;
            var priority = ComputeTargetPriority(t.isMain, hasMain ? mainRect : (RECT?)null, tr);

            // Latch to right edge of target: moving.Left == target.Right
            var distRight = Math.Abs(moving.Left - tr.Right);
            if (distRight <= SnapInPx && HasVerticalOverlap(moving, tr) && IsApproaching(lastRaw, moving, tr, SnapSide.Right))
                Consider(t.hwnd, tr, SnapSide.Right, distRight, priority);

            // Latch to left edge of target: moving.Right == target.Left
            var distLeft = Math.Abs(moving.Right - tr.Left);
            if (distLeft <= SnapInPx && HasVerticalOverlap(moving, tr) && IsApproaching(lastRaw, moving, tr, SnapSide.Left))
                Consider(t.hwnd, tr, SnapSide.Left, distLeft, priority);

            // Latch to bottom edge of target: moving.Top == target.Bottom
            var distBottom = Math.Abs(moving.Top - tr.Bottom);
            if (distBottom <= SnapInPx && HasHorizontalOverlap(moving, tr) && IsApproaching(lastRaw, moving, tr, SnapSide.Bottom))
                Consider(t.hwnd, tr, SnapSide.Bottom, distBottom, priority);

            // Latch to top edge of target: moving.Bottom == target.Top
            var distTop = Math.Abs(moving.Bottom - tr.Top);
            if (distTop <= SnapInPx && HasHorizontalOverlap(moving, tr) && IsApproaching(lastRaw, moving, tr, SnapSide.Top))
                Consider(t.hwnd, tr, SnapSide.Top, distTop, priority);
        }

        best = bestLocal;
        return bestLocal.side != SnapSide.None;

        void Consider(IntPtr hwnd, RECT tr, SnapSide side, int dist, int priority)
        {
            // Score: distance dominates, then target priority.
            var score = (dist * 100) + (priority * 10);
            if (score > bestScore)
                return;

            if (score < bestScore)
            {
                bestLocal = (hwnd, tr, side, dist);
                bestScore = score;
                return;
            }

            // Tie-break: prefer larger overlap (feels more intentional).
            var curOverlap = OverlapArea(moving, bestLocal.targetRect);
            var newOverlap = OverlapArea(moving, tr);
            if (newOverlap > curOverlap)
                bestLocal = (hwnd, tr, side, dist);
        }
    }

    private static int ComputeTargetPriority(bool isMain, RECT? mainRect, RECT targetRect)
    {
        if (isMain)
            return 0;
        if (mainRect is null)
            return 2;

        // If the target is docked/adjacent to main, consider it part of the cluster.
        var mr = mainRect.Value;
        var near =
            Math.Abs(targetRect.Left - mr.Right) <= SnapInPx ||
            Math.Abs(targetRect.Right - mr.Left) <= SnapInPx ||
            Math.Abs(targetRect.Top - mr.Bottom) <= SnapInPx ||
            Math.Abs(targetRect.Bottom - mr.Top) <= SnapInPx;
        return near ? 1 : 2;
    }

    private static bool IsApproaching(RECT? lastRaw, RECT moving, RECT target, SnapSide side)
    {
        if (lastRaw is null)
            return true;

        var prev = lastRaw.Value;
        var prevDist = side switch
        {
            SnapSide.Right => Math.Abs(prev.Left - target.Right),
            SnapSide.Left => Math.Abs(prev.Right - target.Left),
            SnapSide.Bottom => Math.Abs(prev.Top - target.Bottom),
            SnapSide.Top => Math.Abs(prev.Bottom - target.Top),
            _ => int.MaxValue
        };

        var curDist = side switch
        {
            SnapSide.Right => Math.Abs(moving.Left - target.Right),
            SnapSide.Left => Math.Abs(moving.Right - target.Left),
            SnapSide.Bottom => Math.Abs(moving.Top - target.Bottom),
            SnapSide.Top => Math.Abs(moving.Bottom - target.Top),
            _ => int.MaxValue
        };

        // Allow latch when distance is shrinking, or when we just entered the snap band.
        if (prevDist > SnapInPx && curDist <= SnapInPx)
            return true;

        // Small tolerance for cursor jitter / fractional scaling rounding.
        return curDist <= (prevDist + 2);
    }

    private static bool ApplyLatchedMove(ref RECT moving, RECT target, SnapSide side, bool allowAlignMagnets)
    {
        var w = moving.Width;
        var h = moving.Height;
        if (w <= 0 || h <= 0)
            return false;

        switch (side)
        {
            case SnapSide.Right:
            {
                // moving is to the RIGHT of target (touching target.Right)
                moving.Left = target.Right;
                moving.Right = moving.Left + w;
                if (allowAlignMagnets) ApplyVerticalAlignMagnets(ref moving, target);
                return true;
            }
            case SnapSide.Left:
            {
                // moving is to the LEFT of target (touching target.Left)
                moving.Right = target.Left;
                moving.Left = moving.Right - w;
                if (allowAlignMagnets) ApplyVerticalAlignMagnets(ref moving, target);
                return true;
            }
            case SnapSide.Bottom:
            {
                // moving is BELOW target (touching target.Bottom)
                moving.Top = target.Bottom;
                moving.Bottom = moving.Top + h;
                if (allowAlignMagnets) ApplyHorizontalAlignMagnets(ref moving, target);
                return true;
            }
            case SnapSide.Top:
            {
                // moving is ABOVE target (touching target.Top)
                moving.Bottom = target.Top;
                moving.Top = moving.Bottom - h;
                if (allowAlignMagnets) ApplyHorizontalAlignMagnets(ref moving, target);
                return true;
            }
            default:
                return false;
        }
    }

    private static bool ShouldDetach(RECT moving, RECT target, SnapSide side)
    {
        return side switch
        {
            SnapSide.Right => Math.Abs(moving.Left - target.Right) > SnapOutPx,
            SnapSide.Left => Math.Abs(moving.Right - target.Left) > SnapOutPx,
            SnapSide.Bottom => Math.Abs(moving.Top - target.Bottom) > SnapOutPx,
            SnapSide.Top => Math.Abs(moving.Bottom - target.Top) > SnapOutPx,
            _ => true
        };
    }

    private static bool HasAnyLatchedChildren(IntPtr targetHwnd)
    {
        foreach (var kv in _latchedTo)
        {
            if (kv.Value.TargetHwnd == targetHwnd)
                return true;
        }
        return false;
    }

    private static bool IsMainWindow(IntPtr hwnd)
    {
        try
        {
            if (_windows.TryGetValue(hwnd, out var w) && w.WindowRef.TryGetTarget(out var win) && win is not null)
                return win is LyllyPlayer.MainWindow;
        }
        catch { /* ignore */ }
        return false;
    }

    private static bool IsInCluster(IntPtr candidateHwnd, IntPtr rootLeaderHwnd)
    {
        try
        {
            if (candidateHwnd == IntPtr.Zero || rootLeaderHwnd == IntPtr.Zero)
                return false;
            if (candidateHwnd == rootLeaderHwnd)
                return true;

            // Walk the latch chain upward: if it eventually points to the leader, it's in the cluster.
            var cur = candidateHwnd;
            var guard = 0;
            while (guard++ < 32)
            {
                if (!_latchedTo.TryGetValue(cur, out var rel))
                    return false;
                if (rel.TargetHwnd == rootLeaderHwnd)
                    return true;
                cur = rel.TargetHwnd;
                if (cur == IntPtr.Zero)
                    return false;
            }
        }
        catch { /* ignore */ }
        return false;
    }

    private static void PromoteMainToLeader(IntPtr mainHwnd, IntPtr targetHwnd, SnapSide mainLatchedSide)
    {
        try
        {
            if (mainHwnd == IntPtr.Zero || targetHwnd == IntPtr.Zero)
                return;

            // Main should not be a "child" in the latch map.
            _latchedTo.TryRemove(mainHwnd, out _);

            // Attach the target (and thus its existing subtree) under Main.
            var edgeOffset = 0;
            try
            {
                if (TryGetWindowRect(mainHwnd, out var mr) && TryGetWindowRect(targetHwnd, out var tr))
                    edgeOffset = ComputeEdgeOffset(tr, mr, Opposite(mainLatchedSide));
            }
            catch { /* ignore */ }
            _latchedTo[targetHwnd] = new LatchRelation
            {
                TargetHwnd = mainHwnd,
                Side = Opposite(mainLatchedSide),
                EdgeOffsetPx = edgeOffset
            };
        }
        catch
        {
            // ignore
        }
    }

    private static SnapSide Opposite(SnapSide s) => s switch
    {
        SnapSide.Left => SnapSide.Right,
        SnapSide.Right => SnapSide.Left,
        SnapSide.Top => SnapSide.Bottom,
        SnapSide.Bottom => SnapSide.Top,
        _ => SnapSide.None
    };

    private static void TryMoveLatchedCluster(IntPtr rootTargetHwnd, RECT rootRectOverride)
    {
        try
        {
            System.Threading.Interlocked.Exchange(ref _clusterMoveGuard, 1);

            // BFS: move direct children, then children-of-children, etc.
            var q = new Queue<IntPtr>();
            var seen = new HashSet<IntPtr> { rootTargetHwnd };
            var applied = new Dictionary<IntPtr, RECT>(16)
            {
                [rootTargetHwnd] = rootRectOverride
            };
            q.Enqueue(rootTargetHwnd);

            while (q.Count > 0)
            {
                var cur = q.Dequeue();
                foreach (var kv in _latchedTo.ToArray())
                {
                    var child = kv.Key;
                    var rel = kv.Value;
                    if (rel.TargetHwnd != cur)
                        continue;
                    if (!seen.Add(child))
                        continue;
                    if (!TryGetWindowRect(child, out var childRect))
                        continue;
                    if (IsIconic(child) || !IsWindowVisible(child))
                        continue;

                    if (!applied.TryGetValue(cur, out var targetRect))
                    {
                        if (!TryGetWindowRect(cur, out targetRect))
                            continue;
                    }

                    if (!TryComputeLatchedRect(childRect, targetRect, rel.Side, rel.EdgeOffsetPx, out var desired))
                        continue;

                    if (desired.Left == childRect.Left && desired.Top == childRect.Top)
                    {
                        applied[child] = childRect;
                        q.Enqueue(child);
                        continue;
                    }

                    SetWindowPos(child, IntPtr.Zero, desired.Left, desired.Top, 0, 0,
                        SWP_NOZORDER | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOSENDCHANGING | SWP_ASYNCWINDOWPOS);
                    applied[child] = desired;
                    q.Enqueue(child);
                }
            }
        }
        catch
        {
            // ignore
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _clusterMoveGuard, 0);
        }
    }

    private static int ComputeEdgeOffset(RECT child, RECT target, SnapSide side)
    {
        return side switch
        {
            SnapSide.Left => child.Top - target.Top,
            SnapSide.Right => child.Top - target.Top,
            SnapSide.Top => child.Left - target.Left,
            SnapSide.Bottom => child.Left - target.Left,
            _ => 0
        };
    }

    private static bool TryComputeLatchedRect(RECT child, RECT target, SnapSide side, int edgeOffsetPx, out RECT desired)
    {
        desired = child;
        var w = child.Width;
        var h = child.Height;
        if (w <= 0 || h <= 0)
            return false;

        switch (side)
        {
            case SnapSide.Right: // child touches target.Right (child is to the RIGHT)
                desired.Left = target.Right;
                desired.Right = desired.Left + w;
                desired.Top = target.Top + edgeOffsetPx;
                desired.Bottom = desired.Top + h;
                return true;
            case SnapSide.Left: // child touches target.Left (child is to the LEFT)
                desired.Right = target.Left;
                desired.Left = desired.Right - w;
                desired.Top = target.Top + edgeOffsetPx;
                desired.Bottom = desired.Top + h;
                return true;
            case SnapSide.Bottom: // child touches target.Bottom (child is BELOW)
                desired.Top = target.Bottom;
                desired.Bottom = desired.Top + h;
                desired.Left = target.Left + edgeOffsetPx;
                desired.Right = desired.Left + w;
                return true;
            case SnapSide.Top: // child touches target.Top (child is ABOVE)
                desired.Bottom = target.Top;
                desired.Top = desired.Bottom - h;
                desired.Left = target.Left + edgeOffsetPx;
                desired.Right = desired.Left + w;
                return true;
            default:
                return false;
        }
    }

    private static void ApplyVerticalAlignMagnets(ref RECT moving, RECT target)
    {
        // While latched left/right, sliding is vertical. Provide "clicks" for aligning top/bottom with target.
        var h = moving.Height;
        if (h <= 0)
            return;

        var distTop = Math.Abs(moving.Top - target.Top);
        if (distTop <= AlignInPx)
        {
            moving.Top = target.Top;
            moving.Bottom = moving.Top + h;
            return;
        }

        var distBottom = Math.Abs(moving.Bottom - target.Bottom);
        if (distBottom <= AlignInPx)
        {
            moving.Bottom = target.Bottom;
            moving.Top = moving.Bottom - h;
        }
    }

    private static void ApplyHorizontalAlignMagnets(ref RECT moving, RECT target)
    {
        // While latched top/bottom, sliding is horizontal. Provide "clicks" for aligning left/right with target.
        var w = moving.Width;
        if (w <= 0)
            return;

        var distLeft = Math.Abs(moving.Left - target.Left);
        if (distLeft <= AlignInPx)
        {
            moving.Left = target.Left;
            moving.Right = moving.Left + w;
            return;
        }

        var distRight = Math.Abs(moving.Right - target.Right);
        if (distRight <= AlignInPx)
        {
            moving.Right = target.Right;
            moving.Left = moving.Right - w;
        }
    }

    private static bool HasVerticalOverlap(RECT a, RECT b)
    {
        var overlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        return overlap >= MinOverlapPx;
    }

    private static bool HasHorizontalOverlap(RECT a, RECT b)
    {
        var overlap = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        return overlap >= MinOverlapPx;
    }

    private static int OverlapArea(RECT a, RECT b)
    {
        var w = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        var h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        if (w <= 0 || h <= 0) return 0;
        return w * h;
    }

    private static bool TryGetWindowRect(IntPtr hwnd, out RECT rect)
    {
        rect = default;
        try
        {
            return GetWindowRect(hwnd, out rect);
        }
        catch
        {
            rect = default;
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOSENDCHANGING = 0x0400;
    private const uint SWP_ASYNCWINDOWPOS = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}

