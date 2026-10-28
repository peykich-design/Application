package com.peykich.knowledge.domain

import java.time.DayOfWeek
import java.time.OffsetDateTime
import java.time.ZonedDateTime
import java.time.format.DateTimeFormatter
import java.time.temporal.ChronoUnit

object Dates {
    private val ISO: DateTimeFormatter = DateTimeFormatter.ISO_OFFSET_DATE_TIME
    private val DAY: DateTimeFormatter = DateTimeFormatter.ofPattern("yyyy-MM-dd")
    private val HM: DateTimeFormatter = DateTimeFormatter.ofPattern("HH:mm")

    /** frontmatter 用。秒まで（ミリ秒は付けない）。例：2026-10-25T16:40:00+09:00 */
    fun iso(t: ZonedDateTime): String = t.truncatedTo(ChronoUnit.SECONDS).toOffsetDateTime().format(ISO)

    fun parseIso(s: String?): OffsetDateTime? = s?.let { runCatching { OffsetDateTime.parse(it.trim()) }.getOrNull() }

    fun day(t: ZonedDateTime): String = t.format(DAY)

    fun hourMinute(t: ZonedDateTime): String = t.format(HM)

    /** 日記の見出し。例：2026-10-25（日） */
    fun diaryTitle(t: ZonedDateTime): String = "${day(t)}（${weekday(t.dayOfWeek)}）"

    fun weekday(d: DayOfWeek): String = when (d) {
        DayOfWeek.MONDAY -> "月"
        DayOfWeek.TUESDAY -> "火"
        DayOfWeek.WEDNESDAY -> "水"
        DayOfWeek.THURSDAY -> "木"
        DayOfWeek.FRIDAY -> "金"
        DayOfWeek.SATURDAY -> "土"
        DayOfWeek.SUNDAY -> "日"
    }
}
