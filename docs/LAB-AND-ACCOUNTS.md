# מדריך: מעבדת Hyper-V וחשבונות מפתח

> מה צריך בסוף: **שתי מכונות וירטואליות** עם נקודת שחזור נקייה, **חשבון Chrome Web Store** ו**חשבון Microsoft Partner Center** (Edge).
> זמן משוער: מעבדה — שעה וחצי עד שעתיים (רובו התקנות ועדכונים). חשבונות — כחצי שעה.
> כל ההתחברויות, הסיסמאות והתשלומים — שלך. אל תדביק סיסמאות בצ'אט.

## למה צריך את זה

| מה | בשביל |
|----|-------|
| VM עם **Windows 11 Home** (`lab-kids`) | S1 (מדיניות במחשב ביתי לא מנוהל), S2b, S5 (WFP), בדיקות קבלה |
| VM עם **Windows 11 Pro** (`lab-hub`) | S6 (צימוד בין מחשבים), מדיניות ב-Pro |
| חשבונות בשתי החנויות | במחשב שלא בדומיין, Chrome ו-Edge מתקינים בכפייה רק תוסף שפורסם בחנות שלהם |

## מה כבר בדקתי במחשב שלך

| | מצב |
|---|---|
| Hyper-V | **כבר מותקן ופועל** (שירות `vmms` רץ) — אין צורך להפעיל אותו |
| זיכרון | 32GB — מספיק לשתי מכונות של 4GB (ואפילו שלוש) |
| דיסק | 447GB פנויים ב-C: — כל מכונה תתפוס 25–40GB |
| הרשאות | המשתמש שלך **לא** בקבוצת `Hyper-V Administrators`, ולכן אני לא יכול לנהל מכונות בלי הרשאות מנהל |

---

## חלק א' — מעבדת Hyper-V

### א1. הרשאות (פעם אחת)

פתח **PowerShell כמנהל** (קליק ימני → Run as administrator) והרץ:

```powershell
Add-LocalGroupMember -SID S-1-5-32-578 -Member "$env:COMPUTERNAME\$env:USERNAME"
```

אחר כך **התנתק והתחבר מחדש** ל-Windows. מעכשיו אפשר לנהל מכונות וירטואליות בלי הגבהה — גם אתה וגם אני, כשאריץ בדיקות במעבדה.

### א2. הורדת Windows 11

1. היכנס ל-[microsoft.com/software-download/windows11](https://www.microsoft.com/software-download/windows11)
2. תחת **Download Windows 11 Disk Image (ISO) for x64 devices** בחר **Windows 11 (multi-edition ISO)** → Download Now
3. שפה: **English (United States)** — כך הבדיקות האוטומטיות יוכלו לזהות כפתורים לפי שם. (אפשר להוסיף מכונה בעברית בהמשך)
4. שמור כ-`C:\Lab\iso\Win11_x64.iso` (כ-7GB)

ה-ISO הזה כולל גם Home וגם Pro — בוחרים מהדורה בזמן ההתקנה.

### א3. יצירת המכונות

אחרי ההתחברות מחדש, ב-PowerShell **רגיל**:

```powershell
cd C:\Projests\WebFilterApp\WebFilterApp\tests\simulation\lab
```

```powershell
.\New-LabVms.ps1 -IsoPath C:\Lab\iso\Win11_x64.iso
```

הסקריפט יוצר את `lab-hub` ואת `lab-kids`: Generation 2, ‏4GB זיכרון דינמי, 2 מעבדים, דיסק 80GB דינמי, TPM וירטואלי ו-Secure Boot (Windows 11 דורש אותם), מחובר ל-`Default Switch`, ומוכן לאתחל מה-ISO.

### א4. התקנת Windows — לכל מכונה

```powershell
Start-VM -Name lab-kids
```

```powershell
vmconnect localhost lab-kids
```

1. כשמופיע **Press any key to boot from CD or DVD** — לחץ מקש מהר (אם פספסת: Action → Reset)
2. שפה ומקלדת → **Install now**
3. **I don't have a product key** (אפשר להפעיל רישיון אחר כך; ההתקנה עובדת גם בלעדיו, עם מגבלות התאמה אישית)
4. מהדורה: **`lab-kids` → Windows 11 Home** · **`lab-hub` → Windows 11 Pro**
5. **Custom: Install Windows only** → הדיסק הריק → Next
6. בהגדרה הראשונית: אזור, מקלדת, ואז **חשבון Microsoft** — זה "ההורה" (מנהל). מומלץ חשבון Microsoft נפרד לבדיקות, לא החשבון הראשי שלך
7. בשאלות הפרטיות — אפשר לכבות הכול

חזור על אותו דבר עבור `lab-hub`.

### א5. הכנת המכונה (בתוך כל VM)

1. העתק את סקריפט ההכנה לתוך המכונה. מהמחשב שלך, ב-PowerShell רגיל, כשהמכונה דלוקה ומחוברת ל-Windows (מתוך התיקייה `tests\simulation\lab` מסעיף א3):

   ```powershell
   Copy-VMFile -Name lab-kids -SourcePath .\Initialize-LabGuest.ps1 -DestinationPath C:\Lab\Initialize-LabGuest.ps1 -CreateFullPath -FileSource Host
   ```

2. בתוך ה-VM, פתח **PowerShell כמנהל** והרץ:

   ```powershell
   Set-ExecutionPolicy -Scope Process Bypass
   ```

   ```powershell
   C:\Lab\Initialize-LabGuest.ps1 -ComputerName lab-kids -InstallChrome
   ```

   (ב-`lab-hub`: ‏`-ComputerName lab-hub`, בלי Chrome)

   הסקריפט ישאל שתי סיסמאות ויצור:
   - **`labadmin`** — מנהל מקומי, ישמש את הבדיקות האוטומטיות (PowerShell Direct)
   - **`noa`, `yoav`** — חשבונות **רגילים** (לא מנהלים), כמו ילדים אמיתיים
   - פרופיל רשת **Private** (חומת האש של ה-Hub ו-mDNS מוגבלים לרשת פרטית)
   - Chrome (ב-`lab-kids`)

   **שמור את הסיסמאות במנהל הסיסמאות שלך.** כשנגיע לבדיקות אבקש שתזין אותן במקום מקומי — לא בצ'אט.

3. **Settings → Windows Update** → להתקין הכול ולאתחל, עד שאין עוד עדכונים
4. ב-Edge: `edge://settings/help` — לוודא שהוא מעודכן

### א6. נקודת שחזור נקייה

כבה את המכונה (מתוך Windows: Shut down), ואז במחשב שלך:

```powershell
Checkpoint-VM -Name lab-kids -SnapshotName clean
```

```powershell
Checkpoint-VM -Name lab-hub -SnapshotName clean
```

כל בדיקה תתחיל משחזור לנקודה הזו:

```powershell
Restore-VMCheckpoint -VMName lab-kids -Name clean -Confirm:$false
```

### א7. בדיקת תקינות

1. הדלק את שתי המכונות (אחרי האתחול שמחיל את שם המחשב). בתוך `lab-kids` ב-PowerShell:

   ```powershell
   ping -4 lab-hub.local
   ```

   בשורה הראשונה צריכה להופיע כתובת IP — זה אומר שהשם נמצא דרך mDNS. גם אם התשובות עצמן נכשלות (חומת האש חוסמת ping כברירת מחדל), זה בסדר
2. מהמחשב שלך — PowerShell Direct (יבקש את הסיסמה של `labadmin`):

   ```powershell
   Invoke-Command -VMName lab-kids -Credential lab-kids\labadmin -ScriptBlock { hostname; whoami }
   ```

   אמור להחזיר `lab-kids` ו-`lab-kids\labadmin`

### א8. מה לשלוח לי

- שהשלבים א1–א7 עברו (או איפה נתקעת)
- שמות המכונות, אם שינית
- **לא** סיסמאות

### תקלות נפוצות

| תקלה | פתרון |
|------|-------|
| "This PC can't run Windows 11" | המכונה נוצרה בלי TPM/Secure Boot — מחק אותה (`Remove-VM`) והרץ שוב את `New-LabVms.ps1` |
| אין רשת בהתקנה | ודא שהמכונה על `Default Switch`: ‏`Get-VMNetworkAdapter -VMName lab-kids` |
| `ping lab-hub.local` לא מוצא את השם | בשתי המכונות: ‏`Get-NetConnectionProfile` צריך להראות `Private`, ושתיהן צריכות להיות על `Default Switch` |
| `Copy-VMFile` נכשל | המכונה צריכה להיות דלוקה ומחוברת ל-Windows; ה-Guest Service Interface מופעל בסקריפט |
| הרשאות ב-`New-LabVms.ps1` | לא התנתקת והתחברת מחדש אחרי א1 |

---

## חלק ב' — חשבונות מפתח

כרגע רק **פותחים** את החשבונות. את חבילת התוסף לבדיקה, טקסטי החנות ומדיניות הפרטיות אני אכין — ואז אתה מעלה.

### ב1. Chrome Web Store

1. **חשבון Google** — מומלץ חשבון נפרד לפרסום (למשל כתובת Gmail חדשה לפרויקט)
2. הפעל **אימות דו-שלבי** בחשבון: [myaccount.google.com/security](https://myaccount.google.com/security) → 2-Step Verification. **חובה** לפני פרסום או עדכון תוסף
3. היכנס ל-[Chrome Web Store Developer Dashboard](https://chrome.google.com/webstore/devconsole)
4. אשר את הסכם המפתחים ושלם את **דמי ההרשמה החד-פעמיים** (הסכום מוצג בדף; היסטורית 5$)
5. **Account** → שם המפרסם (יוצג בחנות), ואימות כתובת המייל ליצירת קשר
6. אם מופיעה שאלה אם אתה "trader" (סוחר, לפי חוקי האיחוד האירופי) — לפרויקט אישי ללא מכירה: non-trader

**בהמשך (אחרי שאכין חבילה):** New item → העלאת ZIP → Store listing → Privacy practices → **Distribution: Unlisted** (לא מופיע בחיפוש, מותקן רק דרך הקישור או המדיניות) → Submit for review. ההמתנה לביקורת: בדרך כלל ימים; הרשאות רחבות מאריכות אותה.

### ב2. Microsoft Edge Add-ons (Partner Center)

1. **חשבון Microsoft אישי** — לא חשבון עבודה/בית ספר (התוכנית לא תומכת בהם). אפשר את אותו חשבון בדיקות מסעיף א4
2. היכנס ל-[Partner Center — Microsoft Edge](https://partner.microsoft.com/dashboard/microsoftedge/public/login?ref=dd)
3. אם טופס ההרשמה לא נפתח: **Account settings → Programs → Microsoft Edge → Get started**
4. **Account type: Individual** (פשוט יותר; Company דורש אימות ארגון)
5. שם מפרסם ופרטי קשר. **ההרשמה בחינם**

**בהמשך:** Create new extension → ZIP → **Availability → Visibility: Hidden** → Properties → Privacy → Store listings (תיאור באורך 250 תווים לפחות, לוגו 300×300) → Notes for certification → Publish. ההמתנה לאישור: עד 7 ימי עבודה.

### ב3. איך זה ימשיך

1. אני מכין **תוסף מינימלי** לבדיקת S1 — בלי הרשאות רחבות, כדי שהביקורת תהיה מהירה. אחרי שנקבל מזהה (ID) מכל חנות, הגרסה המלאה תעלה **לאותו פריט** כעדכון, והמזהה לא ישתנה
2. אני מכין טקסטי חנות בעברית ובאנגלית, ו**מדיניות פרטיות** (שתי החנויות דורשות אותה, כי התוסף רואה כתובות אתרים; הכול מעובד מקומית). היא צריכה כתובת ציבורית — נחליט ביחד איפה לפרסם אותה
3. אתה מעלה ומגיש בשתי החנויות
4. כשהם מאושרים — שלח לי את **שני מזהי התוסף**. איתם נריץ את S1 במעבדה
