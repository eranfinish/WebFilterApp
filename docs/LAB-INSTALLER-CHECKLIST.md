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

- [ ] `C:\Program Files\WebFilter\appsettings.json` — `"Role": "hub"`, `"ManageBrowserPolicies": true`. לידו `com.webfilter.host.json`.
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

- [ ] קיצור הדרך "WebFilter Admin" בתפריט התחל פותח את ממשק הניהול (UAC). מסך "מכשירים" → קוד צימוד.

## ב. סוכן (`lab-kids`)

- [ ] התקנה שקטה עם צימוד (קוד מסעיף א'). **תנאי סיום M7:** התקנה + צימוד בפחות מ-10 דקות.

  ```powershell
  msiexec /i C:\Lab\WebFilter.msi /qn ROLE=agent HUB=lab-hub.local:47800 PAIRCODE=XXXX-XXXX-XXXX /l*v C:\Lab\agent-install.log
  ```

- [ ] ב-log מופיע `Paired lab-kids with hub`; במסך "מכשירים" ב-Hub מופיע `lab-kids`.
- [ ] `appsettings.json` עם `"Role": "agent"`; **אין** כלל חומת אש "WebFilter Hub".
- [ ] תיקון (`msiexec /fa C:\Lab\WebFilter.msi /qn`) — התפקיד נשאר `agent` והצימוד נשמר.
- [ ] קוד שגוי (מנקודת `clean`): ההתקנה מצליחה, ב-log מופיע `Pairing failed`, וצימוד ידני מ-PowerShell כמנהל עובד:

  ```powershell
  & "C:\Program Files\WebFilter\WebFilter.Service.exe" pair --hub lab-hub.local:47800 --code XXXX-XXXX-XXXX
  ```

- [ ] ב-`noa`: ב-Edge אין InPrivate, ו-`edge://policy` מציג את מדיניות WebFilter (אחרי שהיא קושרה לבן משפחה מוגבל ב-Hub).

## ג. הסרה (בשתי המכונות)

הכנה: `noa` התחברה פעם אחת **והתנתקה** (ה-hive שלה לא טעון), ו-`yoav` **מחובר** (Switch user) בזמן ההסרה. **קודם הסוכן (`lab-kids`), כשה-Hub פועל**, ורק אחר כך ה-Hub — כדי לראות ב-Hub את ביטול הרישום.

- [ ] הסרה:

  ```powershell
  msiexec /x C:\Lab\WebFilter.msi /qn /l*v C:\Lab\uninstall.log
  ```

- [ ] ב-log: `Browser policies: N values removed, 0 left.` ו-`Data folder C:\ProgramData\WebFilter deleted.`
- [ ] ביטול הרישום ב-Hub — בסוכן, ב-log: `Hub: this computer was unregistered; the hub's Devices screen shows it as uninstalled.` (וב-Hub עצמו: `Hub: this computer is not paired with a hub; nothing to unregister.`). ב-Hub, במסך "מכשירים", `lab-kids` עבר ל"מחשבים שהצימוד שלהם בוטל" במצב **"הסינון הוסר"**, ובמסך "פעילות" מופיע "הסינון הוסר ממחשב" עם `lab-kids`. או מהמחשב שלך — ב-`lab-kids` מופיעים `"revoked":true` ו-`"revokedReason":"uninstall"`:

  ```powershell
  .\Invoke-LabAdmin.ps1 -VMName lab-hub -Message '{"type":"getDevices","v":1}'
  ```

- [ ] Hub כבוי (מנקודת `clean`: Hub + סוכן מותקנים ומצומדים): מכבים את ה-Hub (במחשב שלך `Stop-VM -Name lab-hub`) ומסירים את הסוכן. ההסרה מסתיימת בהצלחה, ולכל היותר כ-5 שניות יותר מהרגיל; ב-log `Hub: this computer could not be unregistered (...). It stays on the hub's Devices screen until a parent revokes it there.` אחרי `Start-VM -Name lab-hub` — `lab-kids` עדיין "פעיל" במסך המכשירים, וביטול צימוד ידני שם עובד.
- [ ] לא נשאר כלום: השירות, `C:\Program Files\WebFilter`, `C:\ProgramData\WebFilter`, `HKLM\SOFTWARE\WebFilter`, מפתחות ה-host, ערכי WebFilter תחת `HKLM\SOFTWARE\Policies\{Microsoft\Edge,Google\Chrome}`, `HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot\Network\WebFilter`, כלל חומת האש:

  ```powershell
  Get-Service WebFilter -ErrorAction SilentlyContinue; Test-Path "C:\Program Files\WebFilter", C:\ProgramData\WebFilter, HKLM:\SOFTWARE\WebFilter; reg query HKLM\SOFTWARE\Policies\Microsoft\Edge; Get-NetFirewallRule -DisplayName "WebFilter Hub" -ErrorAction SilentlyContinue
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

## מה לשלוח לי

סימון של כל סעיף (עבר / נכשל), זמני ההתקנה, וקובצי ה-log (`C:\Lab\*.log`) של מה שנכשל.
