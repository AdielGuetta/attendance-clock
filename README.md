# שעון נוכחות | Attendance Clock

מערכת שעון נוכחות: **ASP.NET Core (.NET 10)** + **Microsoft SQL Server** + **React**.
עובד מחתים כניסה ויציאה ורואה את המשמרות שלו. מנהל רואה דוח נוכחות עם סיכומים לפי עובד ולפי יום.
השעה של כל החתמה חדשה מגיעה **משירות API חיצוני עבור `Europe/Zurich`**, ולא מהשעון של השרת או של הדפדפן.

מסמכים נוספים:
[DECISIONS.md](DECISIONS.md) החלטות ומגבלות ·
[docs/TESTING.md](docs/TESTING.md) מה נבדק ומה עבר ·
[docs/FIXES.md](docs/FIXES.md) תיקונים ·
[docs/PRESENTATION.md](docs/PRESENTATION.md) הסבר להצגה ·
[CLAUDE.md](CLAUDE.md) והתיקייה [.claude/](.claude/) הוראות ותוכניות מהפיתוח

## מבנה

```
backend/Attendance.Api/      שרת ASP.NET Core (minimal API)
  Time/                      ספקי זמן חיצוניים, ניסיון חוזר וגיבוי
  Data/                      כל ה-SQL (עם פרמטרים), הקמת המסד
  Domain/                    מודלים, כללי אזור הזמן של ציריך, בניית הדוח
  Database/schema.sql        סכמה אידמפוטנטית (משובצת בשרת)
backend/Attendance.Tests/    xUnit: יחידה ואינטגרציה מול SQL Server אמיתי
frontend/                    React + Vite + TypeScript
scripts/verify-sql.ps1       בדיקות סכמה ונעילות ישירות מול SQL Server
scripts/mock-api.mjs         שרת Node חלופי לבדיקות ממשק בלבד (ראו TESTING.md)
.github/workflows/ci.yml     בנייה ובדיקות ב-GitHub Actions מול SQL Server 2022
```

## דרישות מקדימות

| כלי | גרסה | התקנה ב-Windows |
|-----|------|-----------------|
| .NET SDK | 10.0.100 ומעלה | `winget install Microsoft.DotNet.SDK.10` |
| Node.js | 22 ומעלה | `winget install OpenJS.NodeJS.LTS` |
| SQL Server | 2019 ומעלה (Express מספיק) | `winget install Microsoft.SQLServer.2022.Express`, או Docker (ראו בהמשך) |

השרת צריך גישה יוצאת ב-HTTPS אל `worldtime.timezone.io` ואל `timeapi.io`.

## הקמת מסד הנתונים

### אפשרות א': SQL Server Express (Windows, ברירת המחדל)

מחרוזת החיבור ב-`appsettings.json` מצביעה כבר על `localhost\SQLEXPRESS` עם אימות Windows:

```powershell
dotnet run --project backend/Attendance.Api -- --init-db
```

הפקודה יוצרת את מסד `Attendance`, את הטבלאות (`Employees`, `Shifts`, `Punches`) ואת האינדקסים, ומוסיפה שלושה עובדים לדוגמה.
אפשר להריץ אותה שוב בלי לאבד נתונים. במצב Development השרת מריץ את ההקמה גם בעלייה.

### אפשרות ב': SQL Server ב-Docker

```bash
cp .env.example .env            # אפשר לשנות את הסיסמה
docker compose up -d --wait
```

```powershell
$env:ConnectionStrings__Attendance = "Server=localhost,1433;Database=Attendance;User ID=sa;Password=<הסיסמה מ-.env>;Encrypt=True;TrustServerCertificate=True"
dotnet run --project backend/Attendance.Api -- --init-db
```

`TrustServerCertificate=True` מתאים רק לשרת מקומי.

### בדיקת המסד בלי .NET

```powershell
powershell -ExecutionPolicy Bypass -File scripts/verify-sql.ps1
```

הסקריפט יוצר מסד זמני, בודק את האילוצים ואת הנעילות ומוחק אותו בסיום.

## הרצה מקומית

```powershell
npm --prefix frontend ci
npm --prefix frontend run build      # הפלט נבנה אל backend/Attendance.Api/wwwroot
dotnet run --project backend/Attendance.Api
```

פתחו את http://localhost:5080. בסרגל העליון אפשר לעבור בין **עובד** ל**מנהל** ולבחור עובד לדוגמה.

**מצב פיתוח** עם טעינה מחדש: מריצים את השרת כמו למעלה, ובחלון נוסף:

```powershell
npm --prefix frontend run dev        # http://localhost:5173, בקשות /api עוברות לשרת ב-5080
```

## בדיקות

```powershell
dotnet test --solution backend/Attendance.sln
```

בלי משתנה סביבה רצות רק בדיקות היחידה, ובדיקות האינטגרציה מדולגות. כדי להריץ גם את בדיקות האינטגרציה מול SQL Server:

```powershell
$env:ATTENDANCE_TEST_SQL = "Server=localhost\SQLEXPRESS;Integrated Security=True;Encrypt=True;TrustServerCertificate=True"
dotnet test --solution backend/Attendance.sln
```

הבדיקות יוצרות מסד `AttendanceTest_xxx` ומוחקות אותו בסיום. ב-GitHub Actions כל הבדיקות רצות בכל push.

## ה-API

| שיטה | נתיב | תיאור |
|------|------|-------|
| GET | `/api/employees` | רשימת עובדים |
| GET | `/api/employees/{id}/attendance` | משמרת פתוחה, 30 המשמרות האחרונות ו-`serverNowUtc` |
| POST | `/api/employees/{id}/clock-in` | `{ "requestId": "<uuid>" }` |
| POST | `/api/employees/{id}/clock-out` | `{ "requestId": "<uuid>", "shiftId": "<uuid>" }` |
| GET | `/api/employees/{id}/punches/{requestId}` | האם החתמה נשמרה (משמש אחרי רענון) |
| GET | `/api/report?from=YYYY-MM-DD&to=YYYY-MM-DD` | דוח מנהל (עד 92 ימים, ברירת מחדל היום) |

שגיאות מוחזרות בצורה `{ "code": "...", "error": "הודעה בעברית" }`. הקודים העיקריים:
`time_unavailable` (503), `shift_already_open`, `no_open_shift`, `shift_changed`, `request_reused`, `time_before_start`, `time_before_previous_end` (409).

## פתרון תקלות

- **`An Application Control policy has blocked this file (0x800711C7)`**: מופעל Smart App Control ב-Windows 11, והוא חוסם DLL שנבנה מקומית.
  אפשר לכבות אותו באבטחת Windows, תחת בקרת יישומים ודפדפן ← Smart App Control (בחלק מגרסאות Windows אי אפשר להפעיל אותו מחדש בלי איפוס), או להריץ את הבדיקות ב-GitHub Actions.
- **`time_unavailable`**: אין גישה לשני ספקי הזמן. בודקים חיבור אינטרנט או חומת אש. ההחתמה לא נשמרת, ואפשר לנסות שוב.
- **`database_unavailable`**: בודקים ששירות `MSSQL$SQLEXPRESS` רץ ושמחרוזת החיבור נכונה.

## העלאה ל-GitHub

1. ב-GitHub יוצרים מאגר חדש **ריק**, בלי README, ‏.gitignore או רישיון. לדוגמה `attendance-clock`.
2. מתיקיית `attendance-claude`:

```bash
git remote add origin https://github.com/<USER>/attendance-clock.git
git push -u origin main
```

3. בלשונית **Actions** במאגר נבדק שה-workflow בשם CI עבר. הוא מריץ את כל בדיקות ה-.NET מול SQL Server 2022 ואת בניית ה-React.
4. אם הדחיפה מבקשת התחברות: נכנסים דרך הדפדפן שנפתח (Git Credential Manager), או משתמשים ב-`gh auth login`.

היסטוריית ה-commits נשמרת כמו שהיא. כל שלב בפיתוח מתועד ב-commit נפרד.
