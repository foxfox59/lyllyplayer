package com.lyllyplayer.app.youtube

import okhttp3.OkHttpClient
import okhttp3.RequestBody.Companion.toRequestBody
import org.schabi.newpipe.extractor.downloader.Downloader
import org.schabi.newpipe.extractor.downloader.Request
import org.schabi.newpipe.extractor.downloader.Response
import org.schabi.newpipe.extractor.exceptions.ReCaptchaException
import java.util.concurrent.TimeUnit

/**
 * Minimal OkHttp Downloader for NewPipe Extractor.
 */
class ExtractorDownloader(
    private val client: OkHttpClient = OkHttpClient.Builder()
        .readTimeout(30, TimeUnit.SECONDS)
        .connectTimeout(30, TimeUnit.SECONDS)
        .build(),
) : Downloader() {

    override fun execute(request: Request): Response {
        val url = request.url()
        val builder = okhttp3.Request.Builder().url(url)

        for ((name, values) in request.headers()) {
            for (value in values) {
                builder.addHeader(name, value)
            }
        }
        if (!request.headers().containsKey("User-Agent")) {
            builder.header(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                    "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
            )
        }

        val method = request.httpMethod().uppercase()
        val dataToSend = request.dataToSend()
        when (method) {
            "GET" -> builder.get()
            "HEAD" -> builder.head()
            "POST" -> builder.post((dataToSend ?: ByteArray(0)).toRequestBody(null))
            else -> builder.method(method, dataToSend?.toRequestBody(null))
        }

        val response = client.newCall(builder.build()).execute()
        if (response.code == 429) {
            response.close()
            throw ReCaptchaException("reCaptcha Challenge requested", url)
        }

        val responseHeaders = LinkedHashMap<String, List<String>>()
        for (name in response.headers.names()) {
            responseHeaders[name] = response.headers.values(name)
        }
        val body = response.body?.string() ?: ""
        return Response(
            response.code,
            response.message,
            responseHeaders,
            body,
            response.request.url.toString(),
        )
    }
}
