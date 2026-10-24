// Source of _locales/{en,he}/messages.json: [key, English, Hebrew, description?].
// Edit here, then run `node scripts/locales-source.mjs` to regenerate both files.
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");

const messages = [
  ["extDescription", "Family web filter for home PCs. Works with the WebFilter app on this computer to filter websites and limit browsing time.", "מסנן גלישה משפחתי למחשבים ביתיים. עובד יחד עם התוכנה WebFilter במחשב כדי לסנן אתרים ולהגביל את זמן הגלישה.", "Manifest description and store summary; at most 132 characters."],
  ["actionTitle", "WebFilter", "WebFilter", "Tooltip of the toolbar button."],
  ["langCode", "en", "he", "BCP 47 code of this file; set on <html lang>."],
  ["textDirection", "ltr", "rtl", "Text direction of this file: ltr or rtl."],

  // Reasons (MVP-PLAN §6): one plain sentence each.
  ["reason_allowed_unrestricted", "This account has no restrictions.", "לחשבון הזה אין הגבלות."],
  ["reason_allowed_paused", "Restrictions are paused right now.", "ההגבלות מושהות כרגע."],
  ["reason_allowed_always", "This site is always allowed.", "האתר הזה מותר תמיד."],
  ["reason_allowed_rule", "This site is allowed by a rule.", "האתר הזה מותר לפי כלל."],
  ["reason_allowed_category", "This site is in an allowed category.", "האתר הזה שייך לקטגוריה מותרת."],
  ["reason_allowed_default", "This site is allowed.", "האתר הזה מותר."],
  ["reason_allowed_homework", "This site is allowed in homework mode.", "האתר הזה מותר במצב שיעורי בית."],
  ["reason_allowed_youtube_channel", "This YouTube channel is approved.", "ערוץ ה-YouTube הזה מאושר."],
  ["reason_allowed_browser", "This browser is allowed.", "הדפדפן הזה מותר."],
  ["reason_blocked_rule", "Your parents blocked this site.", "ההורים חסמו את האתר הזה."],
  ["reason_blocked_category", "This site is in a category your parents blocked.", "האתר הזה שייך לקטגוריה שההורים חסמו."],
  ["reason_blocked_not_in_allowlist", "Only sites your parents approved can be opened, and this one isn't on the list yet.", "אפשר לפתוח רק אתרים שההורים אישרו, והאתר הזה עוד לא ברשימה."],
  ["reason_blocked_time_window", "Browsing isn't allowed at this hour.", "בשעה הזו אי אפשר לגלוש."],
  ["reason_blocked_daily_limit", "You've used all of today's browsing time.", "ניצלת את כל זמן הגלישה של היום."],
  ["reason_blocked_homework", "Homework mode is on: only learning sites are open right now.", "מצב שיעורי בית פעיל: עכשיו פתוחים רק אתרי לימוד."],
  ["reason_blocked_ip_literal", "Sites can't be opened by their numeric address.", "אי אפשר לפתוח אתר לפי כתובת מספרית."],
  ["reason_blocked_youtube_channel", "This YouTube channel isn't approved.", "ערוץ ה-YouTube הזה לא מאושר."],
  ["reason_blocked_youtube_unknown", "We couldn't tell which YouTube channel this video belongs to, so it's blocked.", "לא הצלחנו לזהות לאיזה ערוץ YouTube שייך הסרטון, ולכן הוא חסום."],
  ["reason_blocked_youtube_off", "YouTube is turned off for you.", "YouTube כבוי בשבילך."],
  ["reason_blocked_service_unavailable", "The filtering service isn't running, so only always-allowed sites can be opened.", "שירות הסינון לא פועל, ולכן אפשר לפתוח רק אתרים שמותרים תמיד."],
  ["reason_blocked_app_network", "This app isn't allowed to use the internet.", "האפליקציה הזו לא מורשית להתחבר לאינטרנט."],
  ["reason_redirect_youtube_home", "YouTube's home page and search aren't available. Pick one of your channels.", "דף הבית והחיפוש של YouTube לא זמינים. אפשר לבחור באחד הערוצים שלך."],
  // Protection features (PROTECTION-SPEC §6).
  ["reason_blocked_image_search", "Image search is turned off for you.", "חיפוש תמונות כבוי בשבילך."],
  ["reason_blocked_jump", "The site you were on tried to open another site.", "האתר ניסה לפתוח אתר אחר."],
  ["reason_blocked_youtube_shorts", "YouTube Shorts are turned off for you.", "סרטוני Shorts ב-YouTube כבויים בשבילך."],
  // The parents' baseline protection (PROTECTION-SPEC §13).
  ["reason_blocked_baseline_adult", "Adult site — blocked by the family's baseline protection.", "אתר למבוגרים — חסום בהגנה הבסיסית של המשפחה."],
  // YouTube channel search (PROTECTION-SPEC §15).
  ["reason_redirect_youtube_channel_search", "On YouTube you can search for channels only. Here are the channels for your search.", "ב-YouTube אפשר לחפש רק ערוצים. אלה הערוצים שנמצאו בחיפוש שלך."],
  ["reason_failclosed", "WebFilter isn't connected to the filtering service yet, so browsing is blocked. This page will open by itself as soon as it connects.", "WebFilter עוד לא מחובר לשירות הסינון, ולכן הגלישה חסומה. הדף ייפתח לבד ברגע שהחיבור יחזור.", "Static fail-closed ruleset: no profile from the service yet."],
  ["reason_unknown", "This page is blocked.", "הדף הזה חסום."],

  // Block page.
  ["blockedHeading", "This page is blocked", "הדף הזה חסום"],
  ["labelAddress", "Address:", "כתובת:"],
  ["labelCategory", "Category:", "קטגוריה:"],
  ["allowedNowHeading", "This page is allowed now", "הדף הזה מותר עכשיו"],
  ["tryAgain", "Try again", "לנסות שוב"],
  ["overrideButton", "Open anyway", "פתח בכל זאת", "Jump block page, teens and parents: opens the site once; for a teen, the parents see that it was opened."],
  ["baselineHint", "A parent can turn this protection off in the WebFilter admin app, on the Settings page (Baseline protection for parents and adults).", "אפשר לכבות את ההגנה הזו בממשק הניהול של WebFilter, בדף ההגדרות (הגנה בסיסית להורים ולמבוגרים).", "Block page of a parent's own account (PROTECTION-SPEC §13): how to turn the baseline protection off."],
  ["noteLabel", "Why do you need it? (optional)", "למה צריך את זה? (לא חובה)"],
  ["requestButton", "Ask my parents for access", "לבקש גישה מההורים"],
  ["requestYouTubeButton", "Ask my parents to approve this channel", "לבקש מההורים לאשר את הערוץ"],
  ["timeRequest15", "Ask for 15 more minutes", "לבקש עוד 15 דקות"],
  ["timeRequest30", "Ask for 30 more minutes", "לבקש עוד 30 דקות"],
  ["requestSending", "Sending…", "הבקשה נשלחת…"],

  // requestAccess results (tests/simulation/README.md §10) and decisions.
  ["result_created", "Your request was sent to your parents. If they approve, the page will open by itself.", "הבקשה נשלחה להורים. אם יאשרו, הדף ייפתח לבד."],
  ["result_merged", "You already asked for this. The request is waiting for your parents.", "כבר ביקשת את זה. הבקשה מחכה להורים."],
  ["result_rate_limited", "You've sent a lot of requests in the last hour. Try again later.", "שלחת הרבה בקשות בשעה האחרונה. אפשר לנסות שוב מאוחר יותר."],
  ["result_cooldown", "Your parents said no to this a little while ago. You can ask again in an hour.", "ההורים סירבו לבקשה הזו לפני זמן קצר. אפשר לבקש שוב בעוד שעה."],
  ["result_queued", "Your request was saved and will reach your parents when the computer is connected again.", "הבקשה נשמרה ותגיע להורים כשהמחשב יתחבר שוב."],
  ["result_unavailable", "Requests can't be sent right now because the filtering service isn't running.", "אי אפשר לשלוח בקשות כרגע כי שירות הסינון לא פועל."],
  ["result_error", "Something went wrong. Try again in a moment.", "משהו השתבש. אפשר לנסות שוב בעוד רגע."],
  ["result_waiting", "Your request is waiting for your parents.", "הבקשה מחכה להורים."],
  ["decision_approved", "Approved! Opening the page…", "אושר! הדף נפתח…"],
  ["decision_denied", "Your parents said no to this request.", "ההורים סירבו לבקשה."],

  // Time-up page.
  ["timeupHeading", "Browsing time is over", "זמן הגלישה נגמר"],
  ["timeupHeadingWindow", "It's not browsing time now", "עכשיו לא זמן גלישה"],
  ["timeupBodyLimit", "You've used all of today's browsing time.", "ניצלת את כל זמן הגלישה של היום."],
  ["timeupBodyWindow", "Browsing isn't allowed at this hour. You can browse again in your next time window.", "בשעה הזו אי אפשר לגלוש. אפשר יהיה לגלוש שוב בחלון הזמן הבא."],
  ["labelRemaining", "Time left today:", "זמן שנותר היום:"],
  ["labelWindowEnds", "Browsing allowed until:", "הגלישה מותרת עד:"],
  ["timeupAlwaysAllowed", "Sites that are always allowed still work.", "אתרים שמותרים תמיד ממשיכים לעבוד."],
  ["timeupWasAt", "You were on:", "היית בדף:"],
  ["timeupBack", "Back to the page", "חזרה לדף"],

  // My channels.
  ["myChannelsHeading", "My YouTube channels", "הערוצים שלי ב-YouTube"],
  ["myChannelsIntro", "These are the channels your parents approved. YouTube's home page and search aren't available.", "אלה הערוצים שההורים אישרו. דף הבית והחיפוש של YouTube לא זמינים."],
  ["myChannelsEmpty", "No channels have been approved yet. You can ask your parents to add some.", "עדיין לא אושרו ערוצים. אפשר לבקש מההורים להוסיף."],

  // Popup.
  ["statusLoading", "Checking…", "בבדיקה…"],
  ["statusUnknown", "Couldn't read the status.", "לא ניתן לקרוא את המצב."],
  ["statusInert", "Not installed by policy", "לא הותקן על ידי מדיניות"],
  ["statusInertDetail", "WebFilter filters only when the WebFilter app installs it through the browser policy. Here it is inactive and blocks nothing.", "WebFilter מסנן רק כשהתוכנה WebFilter מתקינה אותו דרך מדיניות הדפדפן. כאן הוא לא פעיל ולא חוסם דבר."],
  ["statusService", "Connected to WebFilter", "מחובר ל-WebFilter"],
  ["statusServiceDetail", "Filtering and time limits are active.", "הסינון ומגבלות הזמן פעילים."],
  ["statusBaselineDetail", "The family's baseline protection is on (set in the WebFilter admin app). Nothing of your browsing is recorded.", "ההגנה הבסיסית של המשפחה פעילה (נקבעת בממשק הניהול של WebFilter). שום דבר מהגלישה שלך לא נרשם.", "Popup of a parent's own account (PROTECTION-SPEC §13)."],
  ["statusHost", "The filtering service isn't running", "שירות הסינון לא פועל"],
  ["statusHostDetail", "Working in limited mode until the service is back.", "עובד במצב מוגבל עד שהשירות יחזור."],
  ["statusConnecting", "Connecting…", "בהתחברות…"],
  ["statusDisconnected", "Not connected", "לא מחובר"],
  ["statusDisconnectedDetail", "Browsing is blocked until WebFilter on this computer responds.", "הגלישה חסומה עד ש-WebFilter במחשב יגיב."],
  ["labelUser", "User", "משתמש"],
  ["labelMode", "Filtering", "מצב סינון"],
  ["labelTime", "Browsing time left today", "זמן גלישה שנותר היום"],
  ["labelVersion", "Version", "גרסה"],
  ["labelInstallType", "Install type", "סוג התקנה"],
  ["searchesVisible", "Your searches are visible to your parents", "החיפושים שלך גלויים להורים", "Shown while the search-term log is on (PROTECTION-SPEC §9)."],
  ["adsBlocked", "Ad blocking is on", "חסימת פרסומות פעילה", "Shown while the ad-server ruleset is on (PROTECTION-SPEC §12)."],
  ["mode_unrestricted", "No restrictions", "ללא הגבלה"],
  ["mode_blocklist", "Blocked sites list", "רשימה שחורה"],
  ["mode_allowlist", "Approved sites only", "רק אתרים מאושרים"],
  ["mode_baseline", "Baseline protection for parents", "הגנה בסיסית להורים", "A parent's own account with the family's baseline protection (PROTECTION-SPEC §13)."],
  ["time_unlimited", "No limit", "ללא הגבלה"],
  ["time_expired", "Time is up", "הזמן נגמר"],
  ["time_outside_window", "Outside browsing hours", "מחוץ לשעות הגלישה"],
  ["minutesValue", "$COUNT$ minutes", "$COUNT$ דקות", "count: a number of minutes greater than 1."],
  ["oneMinute", "1 minute", "דקה אחת"],

  // Notifications (MVP-PLAN §8: warnings at 10/5/1 minutes).
  ["notify10", "10 minutes of browsing left today.", "נותרו 10 דקות גלישה להיום."],
  ["notify5", "5 minutes of browsing left today.", "נותרו 5 דקות גלישה להיום."],
  ["notify1", "1 minute of browsing left today.", "נותרה דקה אחת של גלישה להיום."],
  ["notifyApproved", "Your parents approved your request.", "ההורים אישרו את הבקשה שלך."],
  ["notifyDenied", "Your parents said no to your request.", "ההורים סירבו לבקשה שלך."],
];

const placeholders = { minutesValue: { count: { content: "$1", example: "15" } } };

for (const [lang, column] of [["en", 1], ["he", 2]]) {
  const out = {};
  for (const row of messages) {
    const entry = { message: row[column] };
    if (lang === "en" && row[3]) entry.description = row[3];
    if (placeholders[row[0]]) entry.placeholders = placeholders[row[0]];
    out[row[0]] = entry;
  }
  const file = join(root, "_locales", lang, "messages.json");
  mkdirSync(dirname(file), { recursive: true });
  writeFileSync(file, `${JSON.stringify(out, null, 2)}\n`);
}
console.log(`wrote ${messages.length} messages x 2 locales`);
