package com.lyllyplayer.app.ui

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.RectF
import android.graphics.Typeface
import android.text.TextUtils
import android.util.TypedValue
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.ViewGroup
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.graphics.ColorUtils
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import com.lyllyplayer.app.playlist.EntryKind
import com.lyllyplayer.app.playlist.PlaylistEntry
import java.util.Locale
import kotlin.math.roundToInt

/**
 * Native [RecyclerView] playlist. Compose LazyColumn + animateScrollToItem is far too heavy
 * on older devices (smooth-scroll walks every row for seconds).
 */
@Composable
fun PlaylistRecycler(
    entries: List<PlaylistEntry>,
    currentId: String?,
    onPlayEntry: (PlaylistEntry) -> Unit,
    onScrollActiveChanged: (Boolean) -> Unit,
    modifier: Modifier = Modifier,
) {
    val selectedBg = MaterialTheme.colorScheme.surfaceVariant.toArgb()
    val normalBg = MaterialTheme.colorScheme.background.toArgb()
    val onSurface = MaterialTheme.colorScheme.onSurface.toArgb()
    val colors = remember(selectedBg, normalBg, onSurface) {
        PlaylistRvColors(
            selectedBg = selectedBg,
            normalBg = normalBg,
            title = onSurface,
            subtitle = ColorUtils.setAlphaComponent(onSurface, (0.65f * 255).toInt()),
            duration = ColorUtils.setAlphaComponent(onSurface, (0.55f * 255).toInt()),
            thumb = ColorUtils.setAlphaComponent(onSurface, (0.38f * 255).toInt()),
            track = ColorUtils.setAlphaComponent(onSurface, (0.10f * 255).toInt()),
        )
    }

    val onPlayUpdated = rememberUpdatedState(onPlayEntry)
    val onScrollUpdated = rememberUpdatedState(onScrollActiveChanged)

    val adapter = remember {
        PlaylistRvAdapter { entry -> onPlayUpdated.value(entry) }
    }

    var host by remember { mutableStateOf<PlaylistHostView?>(null) }

    LaunchedEffect(entries, currentId, colors) {
        adapter.submit(entries, currentId, colors)
        host?.let { h ->
            h.scrollbar.colors = colors
            // Instant jump when current track is off-screen (never smooth-scroll).
            adapter.scrollToCurrentIfNeeded(h.recycler)
            h.refreshScrollbar()
        }
    }

    AndroidView(
        factory = { context ->
            PlaylistHostView(context).also { h ->
                h.recycler.layoutManager = LinearLayoutManager(context)
                h.recycler.setHasFixedSize(true)
                h.recycler.setItemViewCacheSize(24)
                h.recycler.itemAnimator = null
                h.recycler.overScrollMode = View.OVER_SCROLL_IF_CONTENT_SCROLLS
                h.recycler.adapter = adapter
                h.scrollbar.colors = colors
                h.scrollbar.onJumpFraction = { fraction ->
                    val total = adapter.itemCount
                    if (total > 0) {
                        val target = (total * fraction.coerceIn(0f, 1f))
                            .roundToInt()
                            .coerceIn(0, total - 1)
                        (h.recycler.layoutManager as? LinearLayoutManager)
                            ?.scrollToPositionWithOffset(target, 0)
                        h.refreshScrollbar()
                    }
                }
                h.recycler.addOnScrollListener(
                    object : RecyclerView.OnScrollListener() {
                        override fun onScrollStateChanged(rv: RecyclerView, newState: Int) {
                            onScrollUpdated.value(newState != RecyclerView.SCROLL_STATE_IDLE)
                        }

                        override fun onScrolled(rv: RecyclerView, dx: Int, dy: Int) {
                            h.refreshScrollbar()
                        }
                    },
                )
                host = h
                h.post { h.refreshScrollbar() }
            }
        },
        update = { h ->
            if (h.recycler.adapter !== adapter) h.recycler.adapter = adapter
            h.scrollbar.colors = colors
            host = h
        },
        modifier = modifier.fillMaxSize(),
    )
}

private class PlaylistHostView(context: Context) : FrameLayout(context) {
    val recycler = RecyclerView(context)
    val scrollbar = SlimScrollbarView(context)

    init {
        val density = resources.displayMetrics.density
        val endPad = (14 * density).roundToInt()
        recycler.setPadding(0, 0, endPad, 0)
        recycler.clipToPadding = false
        addView(
            recycler,
            LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.MATCH_PARENT),
        )
        addView(
            scrollbar,
            LayoutParams((20 * density).roundToInt(), LayoutParams.MATCH_PARENT, Gravity.END),
        )
    }

    fun refreshScrollbar() {
        val lm = recycler.layoutManager as? LinearLayoutManager ?: return
        val total = recycler.adapter?.itemCount ?: 0
        if (total <= 0) {
            scrollbar.setMetrics(visible = false, thumbFraction = 1f, scrollFraction = 0f)
            return
        }
        val first = lm.findFirstVisibleItemPosition()
        val last = lm.findLastVisibleItemPosition()
        if (first == RecyclerView.NO_POSITION) {
            scrollbar.setMetrics(visible = false, thumbFraction = 1f, scrollFraction = 0f)
            return
        }
        val canScroll = recycler.canScrollVertically(1) || recycler.canScrollVertically(-1)
        val visible = (last - first + 1).coerceAtLeast(1)
        val thumb = (visible.toFloat() / total.toFloat()).coerceIn(0.12f, 1f)
        val maxIndex = (total - visible).coerceAtLeast(1)
        val firstView = lm.findViewByPosition(first)
        val rowH = firstView?.height?.coerceAtLeast(1) ?: 1
        val offsetFrac = (-(firstView?.top ?: 0)).toFloat() / rowH.toFloat()
        val scroll = ((first + offsetFrac) / maxIndex.toFloat()).coerceIn(0f, 1f)
        scrollbar.setMetrics(visible = canScroll, thumbFraction = thumb, scrollFraction = scroll)
    }
}

@SuppressLint("ClickableViewAccessibility")
private class SlimScrollbarView(context: Context) : View(context) {
    var colors = PlaylistRvColors(0, 0, 0, 0, 0, 0, 0)
        set(value) {
            field = value
            trackPaint.color = value.track
            thumbPaint.color = value.thumb
            invalidate()
        }

    var onJumpFraction: ((Float) -> Unit)? = null

    private var thumbFraction = 0.2f
    private var scrollFraction = 0f
    private var barVisible = false

    private val trackPaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val thumbPaint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val trackRect = RectF()
    private val thumbRect = RectF()

    fun setMetrics(visible: Boolean, thumbFraction: Float, scrollFraction: Float) {
        if (
            barVisible == visible &&
            this.thumbFraction == thumbFraction &&
            this.scrollFraction == scrollFraction
        ) {
            return
        }
        barVisible = visible
        this.thumbFraction = thumbFraction
        this.scrollFraction = scrollFraction
        invalidate()
    }

    override fun onDraw(canvas: Canvas) {
        if (!barVisible || height <= 0) return
        val density = resources.displayMetrics.density
        val trackW = 3f * density
        val left = width - trackW
        val radius = trackW
        trackRect.set(left, 0f, width.toFloat(), height.toFloat())
        canvas.drawRoundRect(trackRect, radius, radius, trackPaint)
        val thumbH = (height * thumbFraction).coerceAtLeast(trackW * 4f)
        val thumbY = (height - thumbH) * scrollFraction
        thumbRect.set(left, thumbY, width.toFloat(), thumbY + thumbH)
        canvas.drawRoundRect(thumbRect, radius, radius, thumbPaint)
    }

    override fun onTouchEvent(event: MotionEvent): Boolean {
        if (!barVisible) return false
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN, MotionEvent.ACTION_MOVE -> {
                val h = height.coerceAtLeast(1)
                onJumpFraction?.invoke(event.y / h.toFloat())
                return true
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> return true
        }
        return super.onTouchEvent(event)
    }
}

private data class PlaylistRvColors(
    val selectedBg: Int,
    val normalBg: Int,
    val title: Int,
    val subtitle: Int,
    val duration: Int,
    val thumb: Int,
    val track: Int,
)

private class PlaylistRvAdapter(
    private val onPlayEntry: (PlaylistEntry) -> Unit,
) : RecyclerView.Adapter<PlaylistRvAdapter.Holder>() {
    private var entries: List<PlaylistEntry> = emptyList()
    private var currentId: String? = null
    private var colors = PlaylistRvColors(0, 0, 0, 0, 0, 0, 0)
    private var lastScrolledId: String? = null

    fun submit(entries: List<PlaylistEntry>, currentId: String?, colors: PlaylistRvColors) {
        val oldId = this.currentId
        val listChanged = this.entries !== entries
        val colorsChanged = this.colors != colors
        this.entries = entries
        this.currentId = currentId
        this.colors = colors
        when {
            listChanged || colorsChanged -> {
                lastScrolledId = null
                notifyDataSetChanged()
            }
            oldId != currentId -> {
                val oldIdx = indexOfId(oldId)
                val newIdx = indexOfId(currentId)
                if (oldIdx >= 0) notifyItemChanged(oldIdx, PAYLOAD_SELECT)
                if (newIdx >= 0 && newIdx != oldIdx) notifyItemChanged(newIdx, PAYLOAD_SELECT)
            }
        }
    }

    fun scrollToCurrentIfNeeded(rv: RecyclerView) {
        val id = currentId ?: return
        if (id == lastScrolledId) return
        val idx = indexOfId(id)
        if (idx < 0) return
        val lm = rv.layoutManager as? LinearLayoutManager ?: return
        val first = lm.findFirstVisibleItemPosition()
        val last = lm.findLastVisibleItemPosition()
        if (first != RecyclerView.NO_POSITION && idx in first..last) {
            lastScrolledId = id
            return
        }
        lastScrolledId = id
        val offset = (rv.height * 0.28f).toInt().coerceAtLeast(0)
        lm.scrollToPositionWithOffset(idx, offset)
    }

    private fun indexOfId(id: String?): Int {
        if (id.isNullOrBlank()) return -1
        return entries.indexOfFirst { it.id.equals(id, ignoreCase = true) }
    }

    override fun getItemCount(): Int = entries.size

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): Holder = Holder(parent.context)

    override fun onBindViewHolder(holder: Holder, position: Int) = bindFull(holder, position)

    override fun onBindViewHolder(holder: Holder, position: Int, payloads: MutableList<Any>) {
        if (payloads.contains(PAYLOAD_SELECT)) {
            holder.applySelection(isSelected(position), colors)
        } else {
            bindFull(holder, position)
        }
    }

    private fun bindFull(holder: Holder, position: Int) {
        val entry = entries[position]
        holder.bind(entry, isSelected(position), colors)
        holder.itemView.setOnClickListener { onPlayEntry(entry) }
    }

    private fun isSelected(position: Int): Boolean {
        val id = currentId ?: return false
        return entries[position].id.equals(id, ignoreCase = true)
    }

    class Holder(context: Context) : RecyclerView.ViewHolder(createRow(context)) {
        private val root = itemView as LinearLayout
        private val title = root.findViewWithTag<TextView>(TAG_TITLE)
        private val subtitle = root.findViewWithTag<TextView>(TAG_SUBTITLE)
        private val duration = root.findViewWithTag<TextView>(TAG_DURATION)

        fun bind(entry: PlaylistEntry, selected: Boolean, colors: PlaylistRvColors) {
            title.text = entry.title
            subtitle.text = subtitleFor(entry)
            val dur = entry.durationSeconds
            if (dur != null) {
                duration.visibility = View.VISIBLE
                duration.text = formatDuration(dur)
            } else {
                duration.visibility = View.GONE
            }
            applySelection(selected, colors)
            title.setTextColor(colors.title)
            subtitle.setTextColor(colors.subtitle)
            duration.setTextColor(colors.duration)
        }

        fun applySelection(selected: Boolean, colors: PlaylistRvColors) {
            root.setBackgroundColor(if (selected) colors.selectedBg else colors.normalBg)
            title.typeface = if (selected) Typeface.DEFAULT_BOLD else Typeface.DEFAULT
        }

        companion object {
            private const val TAG_TITLE = "title"
            private const val TAG_SUBTITLE = "subtitle"
            private const val TAG_DURATION = "duration"

            private fun createRow(context: Context): View {
                val d = context.resources.displayMetrics.density
                fun dp(v: Int) = (v * d).roundToInt()

                val row = LinearLayout(context).apply {
                    orientation = LinearLayout.HORIZONTAL
                    gravity = Gravity.CENTER_VERTICAL
                    layoutParams = RecyclerView.LayoutParams(
                        ViewGroup.LayoutParams.MATCH_PARENT,
                        dp(52),
                    )
                    setPadding(dp(16), 0, dp(16), 0)
                }
                val textCol = LinearLayout(context).apply {
                    orientation = LinearLayout.VERTICAL
                    layoutParams = LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f)
                }
                val title = TextView(context).apply {
                    tag = TAG_TITLE
                    setTextSize(TypedValue.COMPLEX_UNIT_SP, 14f)
                    maxLines = 1
                    ellipsize = TextUtils.TruncateAt.END
                }
                val subtitle = TextView(context).apply {
                    tag = TAG_SUBTITLE
                    setTextSize(TypedValue.COMPLEX_UNIT_SP, 12f)
                    maxLines = 1
                    ellipsize = TextUtils.TruncateAt.END
                }
                textCol.addView(title)
                textCol.addView(subtitle)
                val duration = TextView(context).apply {
                    tag = TAG_DURATION
                    setTextSize(TypedValue.COMPLEX_UNIT_SP, 11f)
                    layoutParams = LinearLayout.LayoutParams(
                        ViewGroup.LayoutParams.WRAP_CONTENT,
                        ViewGroup.LayoutParams.WRAP_CONTENT,
                    ).also { it.marginStart = dp(8) }
                }
                row.addView(textCol)
                row.addView(duration)
                return row
            }
        }
    }

    companion object {
        private const val PAYLOAD_SELECT = "select"

        private fun subtitleFor(entry: PlaylistEntry): String {
            val kind = when (entry.kind) {
                EntryKind.Youtube -> "YouTube"
                EntryKind.Stream -> "Stream"
                EntryKind.Local -> "Local"
            }
            val channel = entry.channel?.takeIf { it.isNotBlank() }
            return if (channel != null) "$channel · $kind" else kind
        }

        private fun formatDuration(seconds: Int): String {
            val totalSec = seconds.coerceAtLeast(0)
            return String.format(Locale.US, "%d:%02d", totalSec / 60, totalSec % 60)
        }
    }
}
