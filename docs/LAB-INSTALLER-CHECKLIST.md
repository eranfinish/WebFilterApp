# בדיקת המתקין במעבדה (M7)

> בודקים את ה-MSI רק במכונות המעבדה (`lab-hub`, `lab-kids` — ראו [LAB-AND-ACCOUNTS.md](LAB-AND-ACCOUNTS.md)), **לא** במחשב הפיתוח.
> כל התקנה, תיקון או הסרה שקטים (`/qn`) רצים עם `REBOOT=ReallySuppress`, ובודקים את קוד היציאה: 0 תקין, 3010 = צריך הפעלה מחדש. בלי זה, msiexec שקט מפעיל מחדש את המכונה בעצמו אם קובץ היה בשימוש (קרה ב-`lab-hub` ב-2026-10-23, קוד 1641, כשמשתמש היה מחובר).
> כל בדיקה מתחילה מנקודת השחזור `clean`. בתוך המכונות — PowerShell **כמנהל** (`labadmin`).
> מה המתקין עושה: [installer/README.md](../installer/README.md).

## הכנה (במחשב שלך)

```powershell
dotnet build installer\WebFilter.Installer.wixproj -c Release
```

```powershell
Copy-VMFile -Name lab-hub -SourcePath installer\bin\Release\en-US\WebFilter.msi -DestinationPath C:\Lab\WebFilter.msi -CreateFullPath -FileSource Host
```

(אותו דבר ל-`lab-kids`. ה-MSI בעברית: `installer\bin\Release\he-IL\WebFilter.msi` — כדאי להתקין אותו פעם אחת דרך הממשק כדי לראות את הדיאלוגים.)

**אחרי כל שחזור לנקודת `clean`:** אחרי אתחול או התחברות מחדש במחשב שלך, ה-`Default Switch` מקבל טווח כתובות חדש, והמכונות רואות "רשת חדשה" שמסווגת **Public**. כלל חומת האש של ה-Hub פתוח רק ברשת פרטית, ולכן הסוכן לא יגיע ל-Hub. בשתי המכונות:

```powershell
Get-NetConnectionProfile | Set-NetConnectionProfile -NetworkCategory Private
```

(רוצים לבדוק במכוון "Hub לא זמין"? מתקינים את הסוכן לפני השינוי: ההתקנה מצליחה, ב-log מופיע `Pairing failed`, והשירות ממשיך לרוץ.)

**אוטומציה מהמחשב שלך:** `tests\simulation\lab\Save-LabCredential.ps1` שומר את סיסמת `labadmin` (אחרי בדיקה מול המכונה), ו-`Invoke-LabAdmin.ps1` שולח הודעות ל-admin pipe בתוך מכונה — למשל קוד צימוד:

```powershell
.\Invoke-LabAdmin.ps1 -VMName lab-hub -Message '{"type":"createPairingCode","v":1}'
```

## א. Hub (`lab-hub`)

- [ ] התקנה דרך הממשק: לחיצה כפולה על ה-MSI → "Parents' computer (hub)". למדוד זמן.
- [ ] השירות רץ, אוטומטי, כ-LocalSystem, עם הפעלה מחדש בכשל:

  ```powershell
  Get-Service WebFilter | Select-Object Status, StartType
  ```

  ```powershell
  sc.exe qc WebFilter; sc.exe qfailure WebFilter
  ```

- [ ] `C:\Program Files\WebFilter\appsettings.json` — `"Role": "hub"`, `"ManageBrowserPolicies": true`, `"ManageNetwork": true`. לידו `com.webfilter.host.json` ו-`WebFilter.Wfp.Native.dll`.
- [ ] רישום ה-host ומדיניות המכונה:

  ```powershell
  reg query HKLM\SOFTWARE\Microsoft\Edge\NativeMessagingHosts\com.webfilter.host; reg query HKLM\SOFTWARE\Google\Chrome\NativeMessagingHosts\com.webfilter.host; reg query HKLM\SOFTWARE\Policies\Microsoft\Edge
  ```

- [ ] חומת אש — פורט 47800, TCP, רשת פרטית בלבד:

  ```powershell
  Get-NetFirewallRule -DisplayName "WebFilter Hub" | Select-Object Enabled, Profile, Direction; Get-NetFirewallRule -DisplayName "WebFilter Hub" | Get-NetFirewallPortFilter
  ```

- [ ] תיקיית הנתונים — רק SYSTEM ו-Administrators. ואז בחשבון `noa` (רגיל): `dir C:\ProgramData\WebFilter\data` נכשל ב-Access denied.

  ```powershell
  icacls C:\ProgramData\WebFilter
  ```

- [ ] הפעלה מחדש אחרי קריסה: אחרי הפקודה, תוך כ-10 שניות השירות שוב `Running`.

  ```powershell
  taskkill /f /im WebFilter.Service.exe
  ```

- [ ] הפעלה מחדש אחרי כשל של רכיב (`FaultExit`, לא קריסה): תופסים את שם ה-admin pipe לפני שהשירות עולה, ולכן `AdminPipeServer` נכשל מיד. צפוי: אירוע 7031 חדש ("terminated unexpectedly", "Restart the service"), וב-Application log של `WebFilter.Service` השורה `AdminPipeServer stopped unexpectedly; ending the process with exit code 1`:

  ```powershell
  Stop-Service WebFilter; $pipe = New-Object System.IO.Pipes.NamedPipeServerStream('WebFilter.Admin', 'InOut', 1); Start-Service WebFilter; Start-Sleep 5; Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Service Control Manager'; Id=7031} -MaxEvents 1 | Format-List TimeCreated, Message
  ```

  משחררים את השם (באותו חלון): תוך כ-15 שניות השירות `Running` ונשאר כך.

  ```powershell
  $pipe.Dispose(); Start-Sleep 15; Get-Service WebFilter; Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='WebFilter.Service'} -MaxEvents 20 | Where-Object Message -like '*exit code 1*' | Select-Object -First 1 | Format-List TimeCreated, Message
  ```

  ועצירה רגילה נשארת "נקייה": אחרי `Stop-Service WebFilter` השירות נשאר `Stopped` ואין אירוע 7031 חדש. אחר כך `Start-Service WebFilter`.

- [ ] קיצור הדרך "WebFilter Admin" בתפריט התחל פותח את ממשק הניהול (UAC). מסך "מכשירים" → קוד צימוד.
- [ ] ההתראות להורים (PROTECTION-SPEC §10): הערך `WebFilter Notifier` תחת `Run` מצביע על `"C:\Program Files\WebFilter\WebFilter.Notifier.exe"`. אחרי התחברות מחדש כ-`labadmin` — סמל המגן של WebFilter באזור ההתראות, ובתפריט שלו "פתח את ניהול המשפחה" ו"יציאה". בהתחברות של חשבון רגיל ל-`lab-hub` אין תהליך `WebFilter.Notifier` (הוא יוצא מיד, ב-`%LOCALAPPDATA%\WebFilter\notifier.log` שלו: `not an administrator`), והוא גם לא נרשם להודעות: בחשבון הרגיל השורה השנייה למטה מחזירה שגיאה.

  ```powershell
  reg query HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run /v "WebFilter Notifier"; Get-Process WebFilter.Notifier -IncludeUserName -ErrorAction SilentlyContinue
  reg query HKCU\Software\Classes\AppUserModelId\WebFilter.Notifier
  ```

- [ ] אחרי ההתקנה וההגדרה הראשונית **אין** הודעה "נמצא חשבון Windows חדש" על החשבונות שכבר היו ב-`lab-hub`: הם הדיווח הראשון של ה-Hub על עצמו, והאשף מקשר אותם (חשבון חדש שנוצר אחר כך כן מודיע — סעיף ב').

- [ ] הודעה על בקשה (אחרי שהסוכן מצומד, סעיף ב'): בקשת גישה מ-`noa` ב-`lab-kids` מציגה ב-`lab-hub` תוך כ-30 שניות הודעת Windows (toast) "בקשה חדשה" בשם **WebFilter** עם סמל המגן (לא שם כללי), הטקסט מימין לשמאל ושם האתר והמחשב שלמים, והכפתור "פתח את ניהול המשפחה". אחרי כמה שניות היא יורדת מהמסך ו**נשארת** במרכז ההודעות (Win+N) תחת WebFilter. לחיצה עליה שם, או על הכפתור, → UAC → ממשק הניהול נפתח, וההודעה יוצאת ממרכז ההודעות. הרישום נמצא ב-HKCU של `labadmin` בלבד: `DisplayName` הוא `WebFilter`, `IconUri` מצביע על `Icon.png` תחת `%LOCALAPPDATA%\ToastNotificationManagerCompat\Apps\WebFilter.Notifier`, `CustomActivator` הוא `{10ba0946-57a5-4d37-7c5f-89f39a01c46b}`, ושרת ה-COM שלו מפעיל את `WebFilter.Notifier.exe` עם `-ToastActivated`:

  ```powershell
  reg query HKCU\Software\Classes\AppUserModelId\WebFilter.Notifier; reg query "HKCU\Software\Classes\CLSID\{10ba0946-57a5-4d37-7c5f-89f39a01c46b}\LocalServer32"
  ```

- [ ] שינוי בסינון נשאר על המסך: ב-`lab-hub`, ב-PowerShell כמנהל, עוצרים את השירות בכוח. הוא עולה מחדש אחרי כ-10 שניות ומדווח שנעצר באופן לא נקי, ותוך כ-30 שניות נוספות מופיעה הודעה "שירות הסינון נעצר" עם "פתח את ניהול המשפחה" ו"סגור". היא **לא** יורדת מהמסך מעצמה (מחכים דקה). "סגור" מוריד אותה, גם ממרכז ההודעות, בלי לפתוח דבר. עוצרים שוב: הפעם "פתח את ניהול המשפחה" → UAC → ממשק הניהול.

  ```powershell
  Stop-Process -Name WebFilter.Service -Force
  ```

- [ ] לחיצה כשההתראות לא רצות: משאירים הודעה על בקשה במרכז ההודעות, בוחרים "יציאה" בתפריט של סמל המגן (אין תהליך `WebFilter.Notifier`), ולוחצים על ההודעה ב-Win+N. צפוי: UAC → ממשק הניהול, וסמל המגן חוזר: Windows הפעיל את ההתראות בשביל הלחיצה, והן ממשיכות לפעול — תהליך אחד, ובשורת הפקודה שלו `-ToastActivated`:

  ```powershell
  Get-CimInstance Win32_Process -Filter "Name='WebFilter.Notifier.exe'" | Select-Object ProcessId, CommandLine
  ```

- [ ] אותו דבר אחרי אתחול: משאירים הודעה במרכז ההודעות, מאתחלים את `lab-hub` ומתחברים כ-`labadmin`; ההודעה עדיין ב-Win+N. לוחצים עליה מיד בכניסה, לפני שסמל המגן מופיע (או אחרי `Stop-Process -Name WebFilter.Notifier` בחלון רגיל): UAC → ממשק הניהול, ובסוף רץ תהליך `WebFilter.Notifier` **אחד** בלבד (הפקודה הקודמת). ב-`notifier.log` אין שורות על הודעות (`toast`).

## ב. סוכן (`lab-kids`)

- [ ] התקנה שקטה עם צימוד (קוד מסעיף א'). **תנאי סיום M7:** התקנה + צימוד בפחות מ-10 דקות.

  ```powershell
  msiexec /i C:\Lab\WebFilter.msi /qn REBOOT=ReallySuppress ROLE=agent HUB=lab-hub.local:47800 PAIRCODE=XXXX-XXXX-XXXX /l*v C:\Lab\agent-install.log
  ```

- [ ] ב-log מופיע `Paired lab-kids with hub`; במסך "מכשירים" ב-Hub מופיע `lab-kids`.
- [ ] הצימוד לא מביא הודעה "נמצא חשבון Windows חדש" ב-`lab-hub` (מחכים שתי דקות): החשבונות שכבר היו ב-`lab-kids` הם הדיווח הראשון שלו, וההורה עונה עליהם במסך "מכשירים" (שם יש לרגילים שבהם כרטיס).
- [ ] `appsettings.json` עם `"Role": "agent"` ו-`"ManageNetwork": true`; **אין** כלל חומת אש "WebFilter Hub", ו**אין** ערך `WebFilter Notifier` תחת `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run`.
- [ ] תיקון (`msiexec /fa C:\Lab\WebFilter.msi /qn REBOOT=ReallySuppress`) — התפקיד נשאר `agent` והצימוד נשמר.
- [ ] קוד שגוי (מנקודת `clean`): ההתקנה מצליחה, ב-log מופיע `Pairing failed`, וצימוד ידני מ-PowerShell כמנהל עובד:

  ```powershell
  & "C:\Program Files\WebFilter\WebFilter.Service.exe" pair --hub lab-hub.local:47800 --code XXXX-XXXX-XXXX
  ```

- [ ] רשת לפי תוכנה (WFP): אחרי שהסוכן קיבל הגדרות, לכל חשבון שאינו מנהל יש מסננים, ולמנהלים אין. ב-`noa` (מקושרת לבן משפחה מוגבל): Edge גולש, אבל ב-PowerShell שלה `Invoke-WebRequest https://www.microsoft.com -UseBasicParsing` נכשל ב-"An attempt was made to access a socket in a way forbidden by its access permissions". מספר המסננים של `noa` (לפחות 6 — חסימה, loopback ודפדפן, בשתי השכבות) ושל `labadmin` (0):

  ```powershell
  netsh wfp show filters file=C:\Lab\filters.xml | Out-Null; foreach ($u in 'noa', 'labadmin') { $sid = (Get-LocalUser $u).SID.Value; "$u " + (Select-String -Path C:\Lab\filters.xml -Pattern "for account $sid" -SimpleMatch).Count }
  ```

  והמסננים נשארים כשהשירות עצור (fail closed): אחרי `Stop-Service WebFilter` אותה פקודה מחזירה אותו מספר. אחר כך `Start-Service WebFilter`.
- [ ] ב-`noa`: ב-Edge אין InPrivate, ו-`edge://policy` מציג את מדיניות WebFilter (אחרי שהיא קושרה לבן משפחה מוגבל ב-Hub).
- [ ] מדיניות המשתמש נכתבת מיד בכניסה, לא עד דקה אחריה. השירות יכול לכתוב את המדיניות של `noa` רק כשה-hive שלה טעון, כלומר כשהיא מחוברת, ועכשיו הוא כותב אותה ברגע ש-Windows מדווח על הכניסה.

  הכנה: `noa` מקושרת לבן משפחה מוגבל, ו**מנותקת** (Sign out, לא Switch user). מוחקים את המדיניות שכבר נשמרה ב-hive שלה, כדי שהמדידה תראה מתי היא נכתבת מחדש (אם `reg load` נכשל כי הקובץ בשימוש — Windows עוד לא שחרר את ה-hive; מחכים דקה או מאתחלים):

  ```powershell
  reg load HKU\noa C:\Users\noa\NTUSER.DAT; reg delete HKU\noa\Software\Policies\Microsoft\Edge /f; reg delete HKU\noa\Software\Policies\Google\Chrome /f; reg unload HKU\noa
  ```

  מהמחשב שלך (PowerShell Direct) מפעילים מדידה: היא מחכה שה-hive של `noa` ייטען, ומודדת כמה זמן עבר עד שהמדיניות נכתבה בו:

  ```powershell
  Invoke-Command -VMName lab-kids -Credential lab-kids\labadmin -ScriptBlock { $sid = (Get-LocalUser noa).SID.Value; while (-not (Test-Path "Registry::HKEY_USERS\$sid")) { Start-Sleep -Milliseconds 200 }; $loaded = Get-Date; while (-not (Get-ItemProperty "Registry::HKEY_USERS\$sid\Software\Policies\Microsoft\Edge" -Name InPrivateModeAvailability -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 200 }; 'policies {0:N1} s after the hive loaded' -f ((Get-Date) - $loaded).TotalSeconds }
  ```

  במכונה מתחברים כ-`noa` ופותחים מיד את Edge. צפוי: המדידה מדפיסה פחות מ-5 שניות (בדרך כלל פחות משנייה); ב-Edge אין InPrivate (Ctrl+Shift+N לא פותח חלון), ו-`edge://policy` מציג את המדיניות כבר בפתיחה הראשונה. (לפני התיקון: עד 60 שניות.)
- [ ] הודעה על חשבון Windows חדש (PROTECTION-SPEC §10): `labadmin` מחובר ל-`lab-hub` וסמל המגן שם. ב-`lab-kids`, ב-PowerShell כמנהל, יוצרים חשבון רגיל:

  ```powershell
  New-LocalUser -Name tom -NoPassword; Add-LocalGroupMember -Group Users -Member tom
  ```

  צפוי: ב-`lab-hub`, תוך כדקה (לכל היותר כשתי דקות: הסוכן קורא את החשבונות פעם בדקה ומסנכרן כל 10 שניות, וההתראות שואלות כל 30 שניות), הודעה "נמצא חשבון Windows חדש" עם הטקסט "החשבון tom במחשב lab-kids: הגלישה בו חסומה עד שתבחרו לאיזה בן משפחה הוא שייך." — מימין לשמאל, `tom` ו-`lab-kids` שלמים — והכפתור "פתח את ניהול המשפחה" בלבד (בלי "סגור"). היא יורדת מהמסך אחרי כמה שניות ונשארת במרכז ההודעות (Win+N). לחיצה עליה שם, או על הכפתור, → UAC → ממשק הניהול נפתח **במסך "מכשירים"** (לא בלוח הבקרה), ובקבוצה של `lab-kids` מופיע `tom` עם הכרטיס "נמצא חשבון Windows חדש".
- [ ] אותו חשבון לא מודיע שוב, ומנהל לא מודיע בכלל: `Rename-LocalUser tom -NewName tommy`, ואחריו חשבון מנהל — אין הודעה חדשה במשך שתי דקות, ו-`helper` מופיע במסך "מכשירים" כמנהל, בלי כרטיס:

  ```powershell
  New-LocalUser -Name helper -NoPassword; Add-LocalGroupMember -Group Administrators -Member helper
  ```

  ובמחשב ה-Hub עצמו: אותה פקודה ליצירת `tom` ב-`lab-hub` מביאה את אותה הודעה עם `lab-hub`. ניקוי: עונים בכרטיסים (למשל "להשאיר חסום (אורח)") ומוחקים את החשבונות (`Remove-LocalUser tommy; Remove-LocalUser helper`, וב-`lab-hub` `Remove-LocalUser tom`). ה-Hub שומר במסך "מכשירים" גם חשבונות שנמחקו.

### בקשת גישה ממחשב סוכן (MVP-PLAN §8.1)

ב-2026-10-24 ההחלטה של ההורה הגיעה לסוכן רק ככלל: מסך החסימה ב-`lab-kids` לא חזר לאתר לבד, והילדה לא קיבלה הודעה. עכשיו ה-snapshot מה-Hub מביא גם את ההחלטות, והסוכן מודיע עליהן לדפדפן. הכנה: שתי המכונות בגרסה עם התיקון (בשדרוג: הסנכרון הראשון אחריו מודיע פעם אחת על החלטות של 48 השעות האחרונות שהסוכן עוד לא ראה — מסך חסימה שעוד פתוח מאז עשוי לחזור לאתר לבד, וזה צפוי); ב-`lab-kids`, `noa` מקושרת לילד/ה ב-Hub ומחוברת, ו-Edge שלה עם התוסף 0.3.0 ("מחובר לשירות" בחלון של התוסף); ב-`lab-hub` ממשק הניהול פתוח במסך "בקשות". אתר לבדיקה: אתר ש-`noa` לא יכולה לפתוח, למשל `https://www.ynet.co.il/` (ובכל סעיף אתר אחר, כדי שאישור קודם לא יפתח אותו).

- [ ] **אישור ב-Hub → מסך החסימה ב-`lab-kids` חוזר לאתר תוך שניות, והילדה מקבלת הודעה.** ב-`noa` פותחים את האתר → מסך החסימה → "לבקש גישה מההורים". צפוי: "הבקשה נשלחה להורים. אם יאשרו, הדף ייפתח לבד.", וב-`lab-hub` הבקשה מופיעה במסך "בקשות" (והודעה של אפליקציית ההתראות). מאשרים (לאתר, "תמיד") ורושמים את השעה. צפוי: תוך פחות מ-5 שניות מסך החסימה ב-`lab-kids` מציג "אושר! הדף נפתח…" ועובר לאתר **בלי רענון ידני**, ומופיעה הודעה של Edge "ההורים אישרו את הבקשה שלך.". לרשום כמה שניות עברו.
- [ ] **סירוב.** אותו דבר עם אתר אחר, והפעם "דחייה" ב-Hub. צפוי: תוך שניות מסך החסימה מציג "ההורים סירבו לבקשה." (נשאר במסך החסימה), והודעה "ההורים סירבו לבקשה שלך.".
- [ ] **בלי הודעה כפולה, גם אחרי הפעלה מחדש.** אחרי האישור, ב-`lab-kids` כמנהל: `Restart-Service WebFilter`, ואחרי כ-30 שניות משנים משהו ב-Hub (למשל מוסיפים כלל ל-`noa`). צפוי: Edge של `noa` חוזר לשירות ("מחובר לשירות"), ואין הודעה שנייה "ההורים אישרו…" (ה-snapshot החדש עדיין מביא את ההחלטה, והסוכן זוכר שכבר הודיע עליה; הבדיקות האוטומטיות בודקות את זה גם ישירות).
- [ ] **בקשה שנשמרה כשה-Hub כבוי.** ב-`lab-hub` כמנהל: `Stop-Service WebFilter`. ב-`noa` מבקשים אתר חדש. צפוי: "הבקשה נשמרה ותגיע להורים כשהמחשב יתחבר שוב."; ובקשה שנייה מאותו אתר (דף אחר שלו, בלשונית אחרת) — "כבר ביקשת את זה. הבקשה מחכה להורים.". `Start-Service WebFilter` ב-`lab-hub`: תוך כדקה הבקשה מופיעה במסך "בקשות" **פעם אחת**; המזהה שלה הוא זה שהסוכן נתן (מתחיל ב-`lab-kids-q`), ומשם ההחלטה חוזרת למסך החסימה. מאשרים: שתי הלשוניות חוזרות לאתר לבד תוך שניות. המזהה, מהמחשב שלך:

  ```powershell
  .\Invoke-LabAdmin.ps1 -VMName lab-hub -Message '{"type":"getRequests","v":1,"status":"approved"}'
  ```

### שומר התוסף (PROTECTION-SPEC §14)

דפדפן שרץ אצל ילד בלי חיבור חי מהתוסף מקבל 3 דקות להורדת התוסף, ואחריהן הרשת שלו נחסמת בחשבון הזה עד שהתוסף מתחבר; ההורים מקבלים התראה. הכנה: ב-`lab-kids`, `noa` מקושרת לילד/ה ב-Hub ומחוברת; ב-Edge שלה התוסף 0.3.0 (`edge://extensions`), וב-Chrome עדיין 0.1.0 מהחנות (`chrome://extensions`), שלא מתחבר לשירות. ב-`lab-hub` `labadmin` מחובר וסמל המגן שם. הפקודות — ב-`lab-kids`, ב-PowerShell כמנהל (Switch user ל-`labadmin`, או PowerShell Direct מהמחשב שלך). שומרים שתי פקודות בדיקה:

מסנני WFP של `noa` לכל דפדפן (2 = מותר, בשתי השכבות; 0 = חסום):

```powershell
netsh wfp show filters file=C:\Lab\filters.xml | Out-Null; $sid = (Get-LocalUser noa).SID.Value; foreach ($b in 'chrome.exe', 'msedge.exe') { "$b " + (Select-String -Path C:\Lab\filters.xml -Pattern "\$b for account $sid" -SimpleMatch).Count }
```

מה השומר רשם (ליומן של Windows מגיעות רק האזהרות — החסימה; ההחזרה לא):

```powershell
Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='WebFilter.Service'} -MaxEvents 30 | Where-Object Message -like '*WebFilter extension*' | Format-List TimeCreated, Message
```

- [ ] **Chrome עם 0.1.0 נחסם אחרי 3 דקות, Edge עם 0.3.0 ממשיך.** ב-`noa` פותחים את Chrome ורושמים את השעה. צפוי: Chrome גולש (כלומר מגיע לרשת; התוסף הישן לא מסנן) עד כ-3:00–3:10 דקות מהפתיחה, ואז דף חדש ב-Chrome מקבל שגיאת רשת של הדפדפן (ייתכן `ERR_NETWORK_ACCESS_DENIED` או "אין חיבור לאינטרנט"); לילד אין הסבר מאיתנו — זו המגבלה המתועדת. Edge ממשיך לגלוש (ולסנן) כל הזמן. הפקודה הראשונה: לפני החסימה `chrome.exe 2`, `msedge.exe 2`; אחריה `chrome.exe 0`, `msedge.exe 2`. ביומן: `Chrome runs in S-1-5-21-… without a live connection from the WebFilter extension after the download window`. ומה קורה לדף שכבר היה פתוח בזמן החסימה (סרטון שמתנגן, הורדה)? לרשום אם הוא נקטע מיד או ממשיך (ALE re-authorization של WFP).
- [ ] **ההורה מקבל התראה.** ב-`lab-hub`, תוך כדקה מהחסימה (סנכרון כל 10 שניות, וההתראות שואלות כל 30): הודעה "דפדפן נחסם: התוסף של WebFilter לא פעל" עם הטקסט "במחשב lab-kids הדפדפן Chrome פעל בחשבון מסונן בלי התוסף של WebFilter, ולכן החיבור שלו לרשת נחסם עד שהתוסף יתחבר." — מימין לשמאל, `lab-kids` ו-`Chrome` שלמים, בלי שם הילדה — והכפתורים "פתח את ניהול המשפחה" ו"סגור"; היא **נשארת** על המסך עד שלוחצים. בממשק הניהול, במסך "פעילות" (סוג "ניסיון עקיפה"): "Chrome פעל אצל נועה בלי התוסף של WebFilter, ולכן החיבור שלו לרשת נחסם עד שהתוסף יתחבר", מסומן כהתראה. בזמן חלון ההורדה עצמו **אין** הודעה.
- [ ] **פתיחה מחדש נותנת חלון חדש, בלי הודעה נוספת.** סוגרים את כל החלונות של Chrome ב-`noa` ומוודאים שלא נשאר לה תהליך `chrome.exe` (למשל אם Chrome ממשיך לרוץ ברקע — "יציאה" מהסמל שלו ליד השעון):

  ```powershell
  Get-Process chrome, msedge -IncludeUserName -ErrorAction SilentlyContinue | Group-Object UserName, ProcessName | Select-Object Name, Count
  ```

  פותחים שוב: תוך כ-10 שניות `chrome.exe 2` בפקודה הראשונה, Chrome גולש עוד כ-3 דקות ונחסם שוב. ב-`lab-hub` **אין** הודעה שנייה באותה שעה (פעם בשעה לכל דפדפן וחשבון); ביומן של `lab-kids` יש שוב את האזהרה. אותו דבר כשסוגרים ופותחים מהר (פחות מ-10 שניות בין הסגירה לפתיחה).
- [ ] **Edge אחרי סגירת החלונות.** סוגרים את כל החלונות של Edge ב-`noa` ובודקים בפקודה למעלה אם נשארים לה תהליכי `msedge.exe` (startup boost, "המשך להפעיל הרחבות ברקע"). לרשום: אם נשארים — פתיחה מחדש של Edge לא נחשבת הפעלה חדשה (רלוונטי רק כשהתוסף של Edge לא עובד; החלטה פתוחה: מדיניות `StartupBoostEnabled=0`/`BackgroundModeEnabled=0` לילדים).
- [ ] **החיבור נשבר ל-90 שניות לכל היותר בלי חסימה.** בזמן ש-Edge של `noa` פתוח (אחרי יותר מ-3 דקות), כמנהל: `Stop-Process -Name WebFilter.NativeHost -Force`. צפוי: התוסף מתחבר מחדש תוך שניות, Edge ממשיך לגלוש, `msedge.exe 2` נשאר, ואין אזהרה ביומן ואין הודעה.
- [ ] **החזרה כשהתוסף מתחבר.** כש-0.3.0 מאושר ב-Chrome Web Store: פותחים את Chrome ב-`noa` (חלון חדש) ובודקים ב-`chrome://extensions` מתי התוסף התעדכן. צפוי: בתוך חלון ההורדה התוסף מתעדכן ומתחבר, ו-Chrome לא נחסם; ואם נחסם קודם — תוך שנייה מהחיבור `chrome.exe 2` שוב (ה-hello מעיר את השומר). **למדוד** כמה זמן מהפתיחה עד שהגרסה החדשה מותקנת: אם יותר מ-3 דקות, Chrome של הילדים נשאר חסום גם אחרי האישור (כל פתיחה נחסמת לפני העדכון) — מאריכים את החלון ב-`appsettings.json` (`"ExtensionGrace": "00:10:00"` בסעיף `WebFilter`, ואז `Restart-Service WebFilter`) ומדווחים לי את הזמן שנמדד.
- [ ] **לא חל על מנהלים, מבוגרים ובהשהיה.** `labadmin` פותח Chrome (עם 0.1.0) ב-`lab-kids` ומחכה 5 דקות: גולש, אין אזהרה, אין הודעה. השהיה של `noa` מה-Hub ל-15 דקות: Chrome שלה (גם אם היה חסום) גולש תוך כ-10 שניות; כשההשהיה נגמרת — חלון חדש של 3 דקות ואז חסימה.
- [ ] **מתג חירום.** ב-`C:\Program Files\WebFilter\appsettings.json` מוסיפים בסעיף `WebFilter` את `"ExtensionGuard": false` ומריצים `Restart-Service WebFilter`: Chrome של `noa` כבר לא נחסם (`chrome.exe 2`). מחזירים (מוחקים את השורה, `Restart-Service WebFilter`). שימו לב: תיקון או שדרוג (`configure`) כותב את הקובץ מחדש ומוחק שינוי ידני.

## ג. הסרה (בשתי המכונות)

הכנה: `noa` התחברה פעם אחת **והתנתקה** (ה-hive שלה לא טעון), ו-`yoav` **מחובר** (Switch user) בזמן ההסרה. **קודם הסוכן (`lab-kids`), כשה-Hub פועל**, ורק אחר כך ה-Hub — כדי לראות ב-Hub את ביטול הרישום.

- [ ] הסרה:

  ```powershell
  msiexec /x C:\Lab\WebFilter.msi /qn REBOOT=ReallySuppress /l*v C:\Lab\uninstall.log
  ```

- [ ] ב-log: `Browser policies: N values removed, 0 left.`, `WFP: provider, sublayer and filters removed.` ו-`Data folder C:\ProgramData\WebFilter deleted.`; ו-`netsh wfp show state file=C:\Lab\state.xml` לא מכיל `WebFilter`.
- [ ] ביטול הרישום ב-Hub — בסוכן, ב-log: `Hub: this computer was unregistered; the hub's Devices screen shows it as uninstalled.` (וב-Hub עצמו: `Hub: this computer is not paired with a hub; nothing to unregister.`). ב-Hub, במסך "מכשירים", `lab-kids` עבר ל"מחשבים שהצימוד שלהם בוטל" במצב **"הסינון הוסר"**, ובמסך "פעילות" מופיע "הסינון הוסר ממחשב" עם `lab-kids`. או מהמחשב שלך — ב-`lab-kids` מופיעים `"revoked":true` ו-`"revokedReason":"uninstall"`:

  ```powershell
  .\Invoke-LabAdmin.ps1 -VMName lab-hub -Message '{"type":"getDevices","v":1}'
  ```

- [ ] Hub כבוי (מנקודת `clean`: Hub + סוכן מותקנים ומצומדים): מכבים את ה-Hub (במחשב שלך `Stop-VM -Name lab-hub`) ומסירים את הסוכן. ההסרה מסתיימת בהצלחה, ולכל היותר כ-5 שניות יותר מהרגיל; ב-log `Hub: this computer could not be unregistered (...). It stays on the hub's Devices screen until a parent revokes it there.` אחרי `Start-VM -Name lab-hub` — `lab-kids` עדיין "פעיל" במסך המכשירים, וביטול צימוד ידני שם עובד.
- [ ] לא נשאר כלום: השירות, `C:\Program Files\WebFilter`, `C:\ProgramData\WebFilter`, `HKLM\SOFTWARE\WebFilter`, מפתחות ה-host, ערכי WebFilter תחת `HKLM\SOFTWARE\Policies\{Microsoft\Edge,Google\Chrome}`, `HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot\Network\WebFilter`, כלל חומת האש, וב-Hub גם ערך ה-`Run` של ההתראות ותהליך `WebFilter.Notifier` (ההסרה סוגרת אותו גם כש-`labadmin` מחובר, בלי לבקש אתחול):

  ```powershell
  Get-Service WebFilter -ErrorAction SilentlyContinue; Test-Path "C:\Program Files\WebFilter", C:\ProgramData\WebFilter, HKLM:\SOFTWARE\WebFilter; reg query HKLM\SOFTWARE\Policies\Microsoft\Edge; Get-NetFirewallRule -DisplayName "WebFilter Hub" -ErrorAction SilentlyContinue; reg query HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run /v "WebFilter Notifier"; Get-Process WebFilter.Notifier -ErrorAction SilentlyContinue
  ```

- [ ] ב-Hub, כשההסרה רצה כ-`labadmin`: ב-log מופיעה הפקודה `WebFilter.Notifier.exe" --unregister` וקוד היציאה שלה 0; ההודעות של WebFilter נעלמו ממרכז ההודעות של `labadmin`, ושני המפתחות שלו (סעיף א') ותיקיית `%LOCALAPPDATA%\ToastNotificationManagerCompat\Apps\WebFilter.Notifier` נמחקו — שתי הפקודות של `reg query` מחזירות שגיאה. חשבון מנהל אחר שהשתמש בהתראות שומר את המפתחות שלו: זה צפוי ולא מזיק (`installer/README.md`).

  ```powershell
  reg query HKCU\Software\Classes\AppUserModelId\WebFilter.Notifier; reg query "HKCU\Software\Classes\CLSID\{10ba0946-57a5-4d37-7c5f-89f39a01c46b}"; Test-Path "$env:LOCALAPPDATA\ToastNotificationManagerCompat\Apps\WebFilter.Notifier"
  ```

- [ ] המדיניות של `noa` (לא מחוברת) נמחקה, ואין מפתח `WebFilter.Cleanup.*` שנשאר טעון:

  ```powershell
  reg load HKU\noa C:\Users\noa\NTUSER.DAT; reg query HKU\noa\Software\Policies\Microsoft\Edge; reg unload HKU\noa; reg query HKU
  ```

- [ ] המדיניות של `yoav` (מחובר) נמחקה: `reg query HKU\<SID של yoav>\Software\Policies\Microsoft\Edge` ריק.
- [ ] **תנאי סיום M7:** בחשבון ילד, Edge ו-Chrome חוזרים למצב רגיל — InPrivate זמין, `edge://policy` ריק, התוסף לא מותקן בכפייה, והרשת עובדת לכל האפליקציות.

## ד. שדרוג

- [ ] בונים גרסה חדשה ומתקינים מעל סוכן מצומד; השירות חוזר לרוץ, התפקיד, הצימוד, ההגדרות והמדיניות נשמרים, וב-log **אין** `cleanup`.

  ```powershell
  dotnet build installer\WebFilter.Installer.wixproj -c Release -p:ProductVersion=0.7.1
  ```

- [ ] שדרוג ב-Hub כשהורה מחובר ואפליקציית ההתראות רצה אצלו: שדרוג שקט (`/qn REBOOT=ReallySuppress`) מסתיים בקוד **0**, לא 3010 או 1641; ב-log פעולת `CloseNotifiers` רצה לפני `InstallValidate` ו-`RemoveExistingProducts`; אין `PendingFileRenameOperations` שמזכיר את `C:\Program Files\WebFilter`. ושדרוג בלחיצה כפולה (ממשק מלא) מעלה את אפליקציית ההתראות מחדש מיד בסופו, בחשבון של ההורה, בלי הרשאות מנהל.
- [ ] אותו שדרוג ב-Hub, כש-`labadmin` מחובר ויש הודעה של WebFilter במרכז ההודעות: ההודעה נשארת, הרישום נשאר (`reg query HKCU\Software\Classes\AppUserModelId\WebFilter.Notifier`), וב-log **אין** `--unregister`. בכניסה הבאה ההתראות עולות, ולחיצה על ההודעה הישנה עדיין פותחת את ממשק הניהול.

## ה. מנוע ה-WFP (אוטומטי, מהמחשב שלך)

בודקים את `WebFilter.Wfp.Native.dll` בלי MSI, ב-`lab-kids`, כ-`labadmin` (המסננים חלים על כל SID). הסקריפטים בונים, מעתיקים ומריצים, ובסוף מוחקים כל אובייקט WFP של WebFilter ובודקים שלא נשאר כלום. דורשים את רכיב ה-C++ של Visual Studio.

```powershell
.\tests\simulation\lab\Test-LabWfp.ps1 -VMName lab-kids
```

- [ ] מנוע (כולל אתחול של המכונה): כשרק `curl.exe` מותר — `powershell.exe` נחסם (10013) ב-IPv4 וב-IPv6 ו-`curl.exe` מתחבר; עותק של `curl.exe` נחסם; loopback ו-SYSTEM לא מושפעים; תוכנה שלא מותקנת מדולגת; AppContainer נחסם ומותר לפי שם החבילה; אחרי אתחול המסננים עדיין שם; `RemoveAll` לא משאיר כלום. התוצאות ב-`tests\simulation\lab\results\wfp-engine.json`.

```powershell
.\tests\simulation\lab\Test-LabWfpService.ps1 -VMName lab-kids
```

- [ ] השירות האמיתי עם `ManageNetwork=true` (כתהליך, לצד השירות המותקן): מסננים לכל חשבון שאינו מנהל ולא למנהלים, תוכנה שהותקנה מאוחר מקבלת מסנן, מסננים שנמחקו חוזרים, השהיה דרך ה-admin pipe מסירה את המסננים מיד, והמסננים נשארים כשהשירות נעצר. התוצאות ב-`tests\simulation\lab\results\wfp-service.json`.

## מה לשלוח לי

סימון של כל סעיף (עבר / נכשל), זמני ההתקנה, וקובצי ה-log (`C:\Lab\*.log`) של מה שנכשל.
