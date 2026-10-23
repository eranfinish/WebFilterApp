# בדיקת המתקין במעבדה (M7)

> בודקים את ה-MSI רק במכונות המעבדה (`lab-hub`, `lab-kids` — ראו [LAB-AND-ACCOUNTS.md](LAB-AND-ACCOUNTS.md)), **לא** במחשב הפיתוח.
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
  msiexec /i C:\Lab\WebFilter.msi /qn ROLE=agent HUB=lab-hub.local:47800 PAIRCODE=XXXX-XXXX-XXXX /l*v C:\Lab\agent-install.log
  ```

- [ ] ב-log מופיע `Paired lab-kids with hub`; במסך "מכשירים" ב-Hub מופיע `lab-kids`.
- [ ] `appsettings.json` עם `"Role": "agent"` ו-`"ManageNetwork": true`; **אין** כלל חומת אש "WebFilter Hub", ו**אין** ערך `WebFilter Notifier` תחת `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run`.
- [ ] תיקון (`msiexec /fa C:\Lab\WebFilter.msi /qn`) — התפקיד נשאר `agent` והצימוד נשמר.
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

## ג. הסרה (בשתי המכונות)

הכנה: `noa` התחברה פעם אחת **והתנתקה** (ה-hive שלה לא טעון), ו-`yoav` **מחובר** (Switch user) בזמן ההסרה. **קודם הסוכן (`lab-kids`), כשה-Hub פועל**, ורק אחר כך ה-Hub — כדי לראות ב-Hub את ביטול הרישום.

- [ ] הסרה:

  ```powershell
  msiexec /x C:\Lab\WebFilter.msi /qn /l*v C:\Lab\uninstall.log
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
