# WebFilter

סינון אתרים ובקרת זמן גלישה למחשבי Windows בבית: סוכן בכל מחשב (תוסף דפדפן כפוי, הרשאות רשת לאפליקציות, מגבלות זמן), שמנוהל ממחשב אחד ברשת הביתית.

> גרסה 2 — בפיתוח. הגרסה הקודמת שמורה בתגית `legacy-v1`.

| מסמך | תוכן |
|------|------|
| [docs/MVP-PLAN.md](docs/MVP-PLAN.md) | מטרה, ארכיטקטורה, אבני דרך, החלטות |
| [docs/SIMULATION-PLAN.md](docs/SIMULATION-PLAN.md) | מערך הסימולציות לבדיקת הסינון |
| [tests/simulation/README.md](tests/simulation/README.md) | פורמט התרחישים והסמנטיקה המדויקת |
| [CLAUDE.md](CLAUDE.md) | כללי עבודה בפרויקט |

## בנייה ובדיקות

דרוש .NET SDK 10 ו-Node.js 20+.

```bash
dotnet build WebFilter.slnx
```

```bash
dotnet test WebFilter.slnx
```

```bash
cd tests/simulation/tools && npm install && npm run validate
```

## מבנה

```
src/        WebFilter.Core, Contracts, Service, NativeHost, Admin, Wfp.Native
extension/  תוסף הדפדפן (M3)
installer/  מתקין MSI (M7)
tests/      בדיקות יחידה ומערך הסימולציות
data/       רשימות פתיחה מוכנות
docs/       תוכנית, סימולציות, ומסמכי הגרסה הקודמת
```
