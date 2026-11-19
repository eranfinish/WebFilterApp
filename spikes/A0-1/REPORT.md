# ספייק A0.1 — Device Owner, משתמשים משניים והסתרת אפליקציה (אמולטור Android 12)

**מטרה.** לענות על ארבע שאלות מתוך `docs/ANDROID-PLAN.md` (A0.1):

1. האם אפשר להגדיר Device Owner עם `dpm set-device-owner` כשיש רק משתמש 0 ואין חשבונות?
2. מה עושה `DISALLOW_ADD_USER`?
3. האם ה-DPC יכול ליצור ולנהל משתמשים משניים עם `createAndManageUser`?
4. האם `setApplicationHidden` עובד על חבילה שנבחרה, והאם ההסתרה שורדת אתחול?

**מכשיר.** רק האמולטור `emulator-5554` (AVD `Tablet_A12`, Android 12, API 31, `google_apis_playstore`, `sdk_gphone64_x86_64`). לא נגענו בטאבלט Lenovo, ב-Galaxy A07, ב-Poco או ב-S8, ולא הסרנו את רימון ולא שינינו הגדרה על מכשיר אמיתי.

**מסקנה בקצרה.** על האמולטור נענו כל ארבע השאלות, עם פלט שמור לכל טענה.

- **שאלה 1:** `set-device-owner` הצליח פעמיים, אבל על מכשיר שכבר הושלם (`device_provisioned=1`). לא נבדק מצב שלא הושלם.
- **שאלה 2:** `DISALLOW_ADD_USER` חוסם את `pm create-user` מה-shell ואת "Add user" במסך המשתמשים בהגדרות (הפריט נעשה `enabled="false"` והודעה "Blocked by your IT admin"). אבל **לא** חוסם את `createAndManageUser` של ה-Device Owner. זה משנה את התכנון של A8: ההגבלה מגינה על ממשק המשתמש, לא על ה-DPC.
- **שאלה 3:** `createAndManageUser` יצר משתמשים, ו-`stopUser` ו-`removeUser` עבדו עליהם.
- **שאלה 4:** ההסתרה עובדת (החבילה נעלמת מ-`pm list packages` ו-`monkey` לא מוצא פעילות), ושורדת שני אתחולים. ההוכחה לאתחול השנייה מבוססת על `boot_id` שהשתנה.

הכול נבדק על אמולטור בלבד. הטאבלט האמיתי לא נבדק (ראו סעיף 6).

---

## 1. פרטי הסביבה (לפני כל שינוי)

קובץ ראיה: [evidence/10-baseline.txt](evidence/10-baseline.txt)

```text
List of devices attached
emulator-5554          device product:sdk_gphone64_x86_64 model:sdk_gphone64_x86_64 ...
Users:
	UserInfo{0:Owner:c13} running
Maximum supported users: 4
  Accounts: 0
1            <- settings get global device_provisioned
1            <- settings get secure user_setup_complete
```

גרסה: `getprop ro.build.version.release` = `12`, `ro.build.version.sdk` = `31` (נבדקו בתחילת הסשן).

מצב התחלתי: משתמש 0 בלבד, 0 חשבונות, אין מגבלות משתמש, אין Device Owner. **שימו לב:** המכשיר כבר הושלם (`device_provisioned=1`, `user_setup_complete=1`). זה לא מה שביקשו לבדוק במפורש, אבל זה המצב שהאמולטור הגיע אליו, ולכן נבדק כמו שהוא.

## 2. מה נבנה

קוד: [kotlin/](kotlin/) — אפליקציה מינימלית (Kotlin 2.0.21, AGP 8.9.1, Gradle 8.11.1 מה-wrapper של A0-7, JDK 17).

- `AdminReceiver` — `DeviceAdminReceiver`. `dpm set-device-owner` מצביע עליו.
- `CmdReceiver` — נקודת כניסה לבדיקות, נשלטת מ-adb: `am broadcast -W -n com.webfilter.spike.a01/.CmdReceiver --es op <op>`. כל פעולה מדפיסה שורה ל-logcat תחת התג `A01` ומחזירה את התוצאה ב-`Broadcast completed: ... data="..."`.
- `Harness` — מבצע את הפעולות: `status`, `create` (`createAndManageUser`), `list`, `stop` (`stopUser`), `remove` (`removeUser`), `restrict`/`unrestrict` (`DISALLOW_ADD_USER`), `hide`/`unhide`/`ishidden` (`setApplicationHidden`), `restrictions`.
- `AndroidManifest.xml` — `android:testOnly="true"` (כדי ש-`dpm remove-active-admin` יוכל להסיר את הבעלים, וכדי ש-`adb install -t` יעבוד). הרכיבים `exported="true"`.
- `Ops.kt` — פענוח הפקודות ושמות משתמשי הניסוי. לוגיקה טהורה, בלי טיפוסים של Android.

## 3. בדיקות לפני הריצה על האמולטור

**בדיקות יחידה (JVM):** `OpsTest` — 6 מבחנים על פענוח הפקודות, ייחודיות השמות, זיהוי משתמשי הניסוי ושם החבילה ברירת המחדל.

קובץ ראיה: [evidence/03-gradle-build.txt](evidence/03-gradle-build.txt), ותוצאות XML ב-[evidence/unit-test-results/](evidence/unit-test-results/)

```text
BUILD SUCCESSFUL in 32s
com.webfilter.spike.a01.OpsTest tests=6 failures=0 errors=0 skipped=0
```

המבחנים האלה בודקים רק את הפענוח, לא את ההתנהגות של Android. כל מה שנוגע ל-Device Owner נבדק רק על המכשיר (סעיף 4).

## 4. ממצאים

### 4.1 הגדרת Device Owner (שאלה 1)

קבצי ראיה: [evidence/11-install-and-owner.txt](evidence/11-install-and-owner.txt), [evidence/30-install-owner-again.txt](evidence/30-install-owner-again.txt), [evidence/39-after-reboot-2.txt](evidence/39-after-reboot-2.txt)

```text
Performing Streamed Install
Success
== set-device-owner
Success: Device owner set to package com.webfilter.spike.a01/.AdminReceiver
Active admin set to component com.webfilter.spike.a01/.AdminReceiver
exit=0
```

הניסיון הראשון (אחרי התקנה נקייה) וההגדרה השנייה (אחרי הסרה והתקנה מחדש, [evidence/30](evidence/30-install-owner-again.txt)) הצליחו שניהם. בדיקת `dpm list-owners` אחרי האתחול מראה:

```text
1 owner:
User  0: admin=com.webfilter.spike.a01/.AdminReceiver,DeviceOwner,Affiliated
```

**ממצא:** על האמולטור, `dpm set-device-owner` הצליח עם משתמש 0 בלבד, 0 חשבונות, **ועם המכשיר כבר מוגדר (`device_provisioned=1`)**. לא בדקנו מצב שבו ההשלמה טרם בוצעה, ולא ניסינו מצבים שליליים (חשבון Google, משתמש משני קיים, Device Owner שני). הסיבה שההגדרה הצליחה במכשיר שהושלם לא נבדקה, ולכן אי אפשר להעביר את התוצאה למכשיר של ילד בלי בדיקה נוספת. הבעלות מסומנת `Affiliated` עבור משתמש 0 בלבד.

### 4.2 `DISALLOW_ADD_USER` (שאלה 2)

קבצי ראיה: [evidence/20-phase1-users.txt](evidence/20-phase1-users.txt), [evidence/21-phase2-stop-restrict-hide.txt](evidence/21-phase2-stop-restrict-hide.txt), [evidence/32-restrict-on.txt](evidence/32-restrict-on.txt), [evidence/31-settings-users-restriction-off.xml](evidence/31-settings-users-restriction-off.xml), [evidence/34-settings-users-restriction-on-FRESH.xml](evidence/34-settings-users-restriction-on-FRESH.xml), [evidence/36-settings-tap-disabled-add-user.xml](evidence/36-settings-tap-disabled-add-user.xml), [evidence/41-settings-users-after-unrestrict.xml](evidence/41-settings-users-after-unrestrict.xml)

הפעלה דרך ה-DPC (`addUserRestriction`) — ה-`dumpsys user` מראה את ההגבלה:

```text
    Device policy global restrictions:
      no_add_user
    Device policy local restrictions:
      no_add_user
```

ניסיון `pm create-user` מה-shell **בזמן שההגבלה פעילה** נכשל:

```text
Error: android.os.ServiceSpecificException: Cannot add user: no_add_user is enabled. (code 1)
Error: couldn't create User.
```

אבל `createAndManageUser` של ה-DPC **הצליח בזמן שההגבלה פעילה**:

```text
===== op=restrict
Broadcast completed: result=0, data="DISALLOW_ADD_USER=true userRestrictions=Bundle[...]"
===== op=create
Broadcast completed: result=0, data="OK created name=spike-kid-1795128799337 id=11 handle=UserHandle{11}"
```

**מסך ההגדרות (ממשק המשתמש של הילד).** בשלב הזה נבדק מסך המשתמשים בהגדרות, בשתי מצבי ההגבלה. בכל פעם נסגרה ההגדרות (`force-stop`) ונפתחה מחדש, כי פתיחה מחדש של מופע קיים לא ריענן את המסך (ראו סעיף 5).

| מצב | "Add user" | "Add users from lock screen" | "Add guest" |
|---|---|---|---|
| ללא הגבלה ([31](evidence/31-settings-users-restriction-off.xml)) | `enabled="true"` | מופיע | מופיע |
| עם `no_add_user` ([34](evidence/34-settings-users-restriction-on-FRESH.xml)) | `enabled="false"` | נעלם | נעלם |
| אחרי `unrestrict` ([41](evidence/41-settings-users-after-unrestrict.xml)) | `enabled="true"` | — | — |

לחיצה על "Add user" כשהוא מושבת ([36](evidence/36-settings-tap-disabled-add-user.xml)) הציגה דיאלוג: `Blocked by your IT admin` / `If you have questions, contact your IT admin`.

**ממצא:** `DISALLOW_ADD_USER` חוסם יצירת משתמשים דרך ה-shell (`pm create-user`) ודרך ממשק ההגדרות (הפריט מושבת, ו"Add user" מהמסך הנעול ו"Add guest" נעלמים), אבל **לא** את `createAndManageUser` של ה-Device Owner. לכן ההגבלה מגינה על המשתמש שמתחת ל-DPC, לא על ה-DPC עצמו. מי שמתכנן A8 צריך להניח שההגבלה לא תעצור את יצירת המשתמשים שהאפליקציה עושה, ואם צריך חסימה מפני ה-DPC עצמו, זו לא ההגבלה הזו.

### 4.3 יצירה וניהול של משתמשים משניים (שאלה 3)

קבצים: [evidence/20-phase1-users.txt](evidence/20-phase1-users.txt), [evidence/21-phase2-stop-restrict-hide.txt](evidence/21-phase2-stop-restrict-hide.txt), [evidence/22-phase3-remove-then-reboot-pre.txt](evidence/22-phase3-remove-then-reboot-pre.txt)

- `createAndManageUser` (עם `SKIP_SETUP_WIZARD`, ועם ה-DPC כ-profile owner) הצליח פעמיים: משתמשים 10 ו-11. ה-`pm list users` אישר:
  ```text
  UserInfo{0:Owner:c13} running
  UserInfo{10:spike-kid-1795128797784:400}
  ```
- `stopUser` החזיר `0` (= הצלחה, `UserManager.USER_OPERATION_SUCCESS`) לשני המשתמשים, וה-`dumpsys user` מראה `State: -1` (לא רצים).
- `removeUser` החזיר `true` לשני המשתמשים, ואחריו נשאר רק משתמש 0 ([evidence/22](evidence/22-phase3-remove-then-reboot-pre.txt)).
- **מגבלה שנתקלנו בה:** `UserManager.getUserProfiles()` הציבורי מחזיר רק את המשתמשים בקבוצת הפרופילים של המשתמש הנוכחי (`[0]`), ולכן ה-DPC לא רואה את המשתמשים המשניים ברשימה. לכן ה-DPC שומר את מזהי המשתמשים שהוא יצר ב-`SharedPreferences`, וההסרה והעצירה עובדות לפי המזהים האלה. הרשימה המלאה נבדקה מ-adb (`pm list users`), לא מה-DPC.
- ה-`UserHandle` נבנה מחדש לפי `getUserHandleForUid(id * 100000)` ולא מ-`UserHandle.of`, כי האחרון חבוי ב-SDK הציבורי (סעיף 5).

### 4.4 הסתרת אפליקציה (שאלה 4)

קבצים: [evidence/21-phase2-stop-restrict-hide.txt](evidence/21-phase2-stop-restrict-hide.txt), [evidence/37-hide-and-boot-id-before.txt](evidence/37-hide-and-boot-id-before.txt)

החבילה שנבחרה: `com.android.chrome` (דפדפן מותקן מראש, סעיף 17 בתכנית).

```text
setApplicationHidden(com.android.chrome,true)=true isHidden=true
== pm list packages chrome (after hide, user 0)        <- ריק: החבילה לא מופיעה
== pm list packages -u chrome (after hide)             package:com.android.chrome
== dumpsys package chrome
    User 0: ... installed=true hidden=true ...
== monkey -p com.android.chrome -c android.intent.category.LAUNCHER 1
** No activities found to run, monkey aborted.
```

ביטול ההסתרה (`unhide`) החזיר את החבילה: `setApplicationHidden(com.android.chrome,false)=true`, ו-`pm list packages chrome` מחזיר `package:com.android.chrome` ([evidence/40](evidence/40-cleanup-a.txt)).

**ממצא:** ההסתרה עובדת. החבילה נעלמת מהרשימה הרגילה, `dumpsys` מראה `hidden=true`, והפעלה נכשלת.

### 4.5 ההסתרה ושאר המצב שורדים אתחול? (שאלה 4, המשך)

שני אתחולים נבדקו.

**אתחול ראשון** ([22](evidence/22-phase3-remove-then-reboot-pre.txt), [23](evidence/23-after-reboot.txt)): `adb reboot`, ואחריו `sys.boot_completed=1`. ההוכחה לאתחול הייתה חלשה (`uptime` של 26 שניות בלבד, בלי השוואת `boot_id`). קובץ [24](evidence/24-reboot-proof.txt) מכיל רק אירועי `sysui_multi_action` ואינו מוכיח אתחול, ולכן לא מסתמכים עליו. הממצאים נשארו זהים, אבל ההוכחה לא מספיקה לבד; ההוכחה המלאה היא האתחול השני.

**אתחול שני, עם הוכחה מלאה** ([37](evidence/37-hide-and-boot-id-before.txt) לפני, [39](evidence/39-after-reboot-2.txt) אחרי):

```text
== 37 boot_id BEFORE reboot
ca12e4b5-0177-4710-b6f0-1fe559e9af15
252.48 847.00
== 39 boot_id AFTER reboot
9fd19717-6ebb-40c9-ad29-8161b7eac1bb
26.10 46.77
```

ה-`boot_id` השתנה, כלומר המכשיר אותחל. אחרי האתחול השני:

```text
== dpm list-owners
User  0: admin=com.webfilter.spike.a01/.AdminReceiver,DeviceOwner,Affiliated
== DPC status op -> owner=true admin=true myUser=0 ...
== DPC ishidden op -> isHidden(com.android.chrome)=true
== pm list packages chrome (user 0, expect none)       <- ריק: עדיין מוסתרת
== dumpsys package chrome -> User 0: ... hidden=true ...
== no_add_user after reboot (restriction still on?)
    Device policy global restrictions:
      no_add_user
    Device policy local restrictions:
      no_add_user
```

**ממצא:** ההסתרה שורדת אתחול, וגם ה-Device Owner וגם ההגבלה `DISALLOW_ADD_USER` שורדים אותו. (ההגבלה הייתה פעילה בזמן האתחול השני.)

### 4.6 ניקוי (מצב סופי)

קבצי ראיה: [evidence/40-cleanup-a.txt](evidence/40-cleanup-a.txt), [evidence/41-settings-users-after-unrestrict.xml](evidence/41-settings-users-after-unrestrict.xml), [evidence/42-cleanup-b-final.txt](evidence/42-cleanup-b-final.txt)

- `unrestrict`: `DISALLOW_ADD_USER=false`, ו-`dumpsys user` כבר אינו מציג `no_add_user`.
- `unhide` ל-Chrome: `isHidden=false`.
- ב-Settings, אחרי `unrestrict` ופתיחה מחדש של המסך, "Add user" הוא `enabled="true"` ([41](evidence/41-settings-users-after-unrestrict.xml)).
- `dpm remove-active-admin`: `Success: Admin removed ...`.
- `adb uninstall com.webfilter.spike.a01`: `Success`.
- מצב סופי: `dpm list-owners` → `no owners`; חשבונות: `Accounts: 0`; משתמשים: רק `0`; Chrome גלוי; `device_provisioned=1`.

## 5. מה נכשל ולמה

1. **הרצת Gradle דרך `run_in_terminal`:** הפעלה של `gradlew.bat` דרך `cmd /c` נכשלה עם `'gradlew.bat' is not recognized` (הניסיונות הראשונים נדרסו בקובץ הלוג). הסיבה המדויקת לא אובחנה. הפתרון: הרצת ה-wrapper ישירות: `java -classpath gradle\wrapper\gradle-wrapper.jar org.gradle.wrapper.GradleWrapperMain`. זה אותו wrapper ואותה גרסת Gradle 8.11.1 (הכתובת בלוג: `docs/gradle 8.11.1`).
2. **פלטפורמה 37 לא נמצאה:** `Failed to find Platform SDK with path: platforms;android-37` ([evidence/01-gradle-build.txt](evidence/01-gradle-build.txt)). בדיסק יש `platforms/android-37.0`, ו-AGP 8.9.1 לא מזהה אותה. עברנו ל-`compileSdk = 36` (המקסימום שנבדק עם AGP 8.9.1). AGP התקין אוטומטית את `android-36` ואת build-tools `35.0.0` (לפני שהצמדנו `buildToolsVersion = "36.0.0"`). **זה שינוי ב-SDK של המשתמש מחוץ ל-`spikes/A0-1/`, ולא בוטל.**
3. **הפעלת `compileDebugKotlin` נכשלה** ([evidence/02-gradle-build.txt](evidence/02-gradle-build.txt)): `UserHandle.getIdentifier()` ו-`UserHandle.of(int)` אינם חשופים ב-SDK הציבורי של API 36. התיקון: מזהה המשתמש מ-`UserHandle.toString()` (`UserHandle{10}`), ו-`UserHandle.getUserHandleForUid(id * 100000)` ליצירת ה-handle. התוצאה על המכשיר אישרה את הפענוח: `handle=UserHandle{10}` תואם ל-`pm list users` (משתמש 10).
4. **בנייה שלישית הצליחה** ([evidence/03-gradle-build.txt](evidence/03-gradle-build.txt)) — `BUILD SUCCESSFUL`, ו-6 מבחני היחידה עברו.
5. **הפתיחה הראשונה של מסך ההגדרות לא ריעננה את המסך:** `am start` דיווח `intent has been delivered to currently running top-most instance`, כך שה-dump הראשון ([33](evidence/33-settings-users-restriction-on.xml)) הראה את מצב הביניים. נסגר עם `force-stop` ונפתח מחדש ([34](evidence/34-settings-users-restriction-on-FRESH.xml)); ה-dump המתוקן הוא זה שמצוטט בסעיף 4.2.
6. **לחיצה ראשונה על מיקום שגוי:** ה-tap הראשון על "Add user" ([35](evidence/35-settings-after-tap-add-user-restriction-on.xml)) נעשה לפי קואורדינטות של מסך ישן, ולא פגע בפריט; ה-tap שנעשה לאחר מכן על המיקום העדכני ([36](evidence/36-settings-tap-disabled-add-user.xml)) הוא שנותן את התוצאה.

## 6. מה לא נבדק (ומדוע)

- **הטאבלט Lenovo TB328XU.** לא נגענו בו (הוראה מפורשת). על הטאבלט רימון רשום כמנהל מכשיר, וזה חוסם את ניפוי ה-USB (שורה 331 ברשימת ההערות של התכנית, פריט 10). לכן אי אפשר לדעת אם `set-device-owner` יעבוד שם בלי להסיר את רימון. זו הבדיקה החשובה ביותר שנשארה.
- **מכשיר אמיתי מול אמולטור.** האמולטור הוא image של Google APIs עם Play. ייתכן שההתנהגות של ההגבלות וההסתרה שונה במכשיר של היצרן.
- **מצבים שליליים של `set-device-owner`:** חשבון Google מחובר, משתמש משני קיים, Device Owner שני, ומכשיר שלא הושלם. לא נבדקו.
- **מגבלת מספר המשתמשים:** הגבלה `Maximum supported users: 4`, ו-`running users: 3`. לא הגענו לגבול, ולא בדקנו את קוד השגיאה `USER_OPERATION_ERROR_MAX_USERS`.
- **נתיבי יצירת משתמש נוספים בהגדרות:** נבדקו "Add user" ו-"Add guest" ו-"Add users from lock screen" במסך המשתמשים בלבד. לא נבדקו חיפוש בהגדרות, ולא נבדק אם המסך הנעול מאפשר יצירה בדרך אחרת.
- **משתמש משני והסתרה:** ההסתרה נבדקה רק על משתמש 0. למשתמשים 10 ו-11 Chrome לא מותקן (`installed=false`), ולכן לא נבדק שם.
- **משתמשים מסונפים (affiliation) בין משתמשים:** סעיף 1 בתכנית ("משתמשי ילד מסונפים וקישור בין משתמשים") לא נבדק. ה-`dpm list-owners` מסמן `Affiliated` רק עבור משתמש 0, וזה כל מה שנבדק.
- **הפעלה והחלפה (`startUserInBackground`, `switchUser`):** לא נבדקו (כדי לחסוך RAM).
- **אפליקציות מערכת אחרות** (סעיף 17): נבדקה רק Chrome.
- **בדיקות היחידה** מכסות רק את הפענוח; כל מה שנוגע ל-Android נבדק רק בהרצה על המכשיר.

## 7. החלטה

- **על האמולטור, שאלות 1, 3 ו-4 עברו** (Go עם המגבלות לעיל): Device Owner נקבע על מכשיר שהושלם, משתמשים משניים נוצרו ונעצרו ונמחקו על ידי ה-DPC, והסתרת חבילה שורדת שני אתחולים, עם `boot_id` שונה להוכחה.
- **שאלה 2:** `DISALLOW_ADD_USER` חוסם את ממשק המשתמש ואת ה-shell, אבל לא את ה-DPC. זה לא פגם בתכנון, אלא עובדה שצריך לדעת: ההגבלה מגינה על הילד מפני הוספת משתמשים בהגדרות, לא מפני ה-DPC. A8 צריך להניח את זה.
- **הטאבלט נשאר לא מאומת.** לפני A8 צריך בדיקה נפרדת על הטאבלט, אחרי שרימון יוסר כמנהל מכשיר (בהחלטת הבעלים).

## 8. מצב הקבצים והסביבה

- קוד: [kotlin/](kotlin/)
- ראיות: [evidence/](evidence/) (הפלט הגולמי של כל פקודה, לפי סדר הריצה)
- `.gitignore` של הספייק: [.gitignore](.gitignore) (מוציא build ו-`local.properties`)
- לא נעשה commit ולא push. לא שונה שום קובץ מחוץ ל-`spikes/A0-1/`, אבל AGP התקין `android-36` ו-build-tools `35.0.0` ב-SDK של המשתמש (סעיף 5, פריט 2). לא הוסרו.
- מצב האמולטור בסוף: בלי Device Owner, בלי האפליקציה, משתמש 0 בלבד, 0 חשבונות, Chrome גלוי, המכשיר מוגדר כמו בהתחלה.
