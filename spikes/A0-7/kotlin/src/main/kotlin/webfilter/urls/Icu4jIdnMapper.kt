package webfilter.urls

import com.ibm.icu.text.IDNA

/**
 * UTS-46 with the flags the C# reference matches (corpus, spikes/A0-7):
 * - nontransitional processing: "straße" stays ß (ICU's default is transitional, which gives "ss");
 * - CheckContextJ on: ZWJ/ZWNJ without a valid context are rejected, as the C# does.
 *
 * Not set, because the C# does not apply them: CheckBidi (it accepts "aש.example"; the WHATWG URL Standard
 * would reject it, see REPORT.md), STD3 rules, and CheckHyphens / length checks, which are ignored below.
 *
 * android.icu.text.IDNA on the device must use the same flags and the same error filter.
 */
class Icu4jIdnMapper : IdnMapper {
    private val idna = IDNA.getUTS46Instance(IDNA.NONTRANSITIONAL_TO_ASCII or IDNA.CHECK_CONTEXTJ)

    override fun toAscii(unicode: String): String? {
        val info = IDNA.Info()
        val out = StringBuilder()
        idna.nameToASCII(unicode, out, info)
        // hasErrors() also counts "xx--" hyphens (HYPHEN_3_4), which the C# accepts ("ab--ü.example").
        // Leading and trailing hyphens and labels over 63 characters are still rejected, as in the C#.
        val fatal = info.errors.filter { it != IDNA.Error.HYPHEN_3_4 }
        return if (fatal.isEmpty()) out.toString() else null
    }
}
