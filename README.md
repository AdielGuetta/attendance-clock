# שעון נוכחות | Attendance Clock

מערכת שעון נוכחות: **ASP.NET Core (.NET 10)** + **Microsoft SQL Server** + **React**.
עובד מחתים כניסה ויציאה ורואה את המשמרות שלו. מנהל רואה דוח נוכחות עם סיכומים לפי עובד ולפי יום.
השעה של כל החתמה חדשה מגיעה **משירות API חיצוני עבור `Europe/Zurich`**, ולא מהשעון של השרת או של הדפדפן.

המאגר: https://github.com/AdielGuetta/attendance-clock · מצב הבדיקות: לשונית [Actions](https://github.com/AdielGuetta/attendance-clock/actions)

מסמכים נוספים:
[DECISIONS.md](DECISIONS.md) החלטות ומגבלות ·
[docs/TESTING.md](docs/TESTING.md) מה נבדק ומה עבר ·
[docs/FIXES.md](docs/FIXES.md) תיקונים ·
[docs/PRESENTATION.md](docs/PRESENTATION.md) הסבר להצגה ·
[docs/GUIDE.md](docs/GUIDE.md) מדריך למי שלא תכנת ·
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
scripts/demo-check.sh        בדיקה מקצה לקצה של מערכת רצה: כניסה, רענון, יציאה ודוח
Dockerfile, compose.yaml     כל המערכת ב-Docker (SQL Server + שרת + React)
.github/workflows/ci.yml     בנייה, בדיקות מול SQL Server 2022 והרצה מלאה ב-Docker ב-GitHub Actions
docs/GUIDE.md                מדריך בעברית למי שלא תכנת: מה קורה מהלחיצה ועד הדוח
docs/PRACTICE.md             שני תרגילי שינוי לקראת ההצגה
```

## הרצה מהירה של כל המערכת ב-Docker (מומלץ להצגה)

שלושה רכיבים רצים בסביבת Linux בתוך Docker: **SQL Server 2022**, **שרת ASP.NET Core** והממשק ב-**React**, שהשרת מגיש.
שום קוד .NET לא נבנה ב-Windows עצמו, ולכן זה עובד גם כש-Smart App Control מופעל.

**פעם אחת במחשב** (Windows 10/11, גם Home):
1. ב-PowerShell **כמנהל**: `wsl --install`, ואז מפעילים מחדש את המחשב.
2. מתקינים את Docker Desktop: `winget install Docker.DockerDesktop` (או מ-docker.com), פותחים אותו ומחכים ל-"Engine running".

**בכל הרצה**, מתיקיית המאגר:

```bash
git clone https://github.com/AdielGuetta/attendance-clock.git
cd attendance-clock
cp .env.example .env
docker compose up --build -d --wait
```

פותחים את http://localhost:8080. בהרצה הראשונה ההורדה והבנייה לוקחות כמה דקות, ובהמשך פחות מדקה.
המסד, הטבלאות ושלושת העובדים לדוגמה נוצרים לבד. הנתונים נשמרים ב-volume גם אחרי `docker compose down`.

```bash
bash scripts/demo-check.sh          # בדיקה אוטומטית: כניסה, רענון, יציאה ודוח (צריך curl ו-jq, למשל ב-Git Bash)
docker compose logs -f app          # לוגים של השרת
docker compose down                 # עצירה (להוספת -v כדי למחוק גם את הנתונים)
```

האתר זמין רק מהמחשב הזה, כי הפורט מפורסם על `127.0.0.1` בלבד.
בתוך Docker מבוטלת בדיקת ה-loopback של השרת (`AllowRemoteClients=true`), כי הבקשות מגיעות מרשת Docker.

## דרישות מקדימות להרצה בלי Docker

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

### אפשרות ב': רק SQL Server ב-Docker, והשרת עם dotnet

```bash
cp .env.example .env            # אפשר לשנות את הסיסמה
docker compose up -d --wait sqlserver
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
  במקום לכבות אותו, מריצים את המערכת ב-Docker (ראו למעלה), ואת הבדיקות ב-GitHub Actions.
- **`docker` לא מזוהה, או "WSL 2 is not installed"**: חסר שלב ההתקנה החד-פעמי (`wsl --install` כמנהל, הפעלה מחדש ו-Docker Desktop).
  אם Docker Desktop מדווח שהווירטואליזציה כבויה, מפעילים אותה ב-BIOS/UEFI (Intel VT-x).
- **הפורט 8080 תפוס**: משנים בקובץ `compose.yaml` את `127.0.0.1:8080:8080` ל-`127.0.0.1:8090:8080`, למשל.
- **`time_unavailable`**: אין גישה לשני ספקי הזמן. בודקים חיבור אינטרנט או חומת אש. ההחתמה לא נשמרת, ואפשר לנסות שוב.
- **`database_unavailable`**: בודקים ששירות `MSSQL$SQLEXPRESS` רץ ושמחרוזת החיבור נכונה.

## איך הפרויקט נבנה עם Claude Code

**מה Claude Code עשה:** קרא את פרויקט הדוגמה, כתב תוכנית עבודה ([.claude/plans/](.claude/plans/)) ומימש אותה בשלבים, עם commit לכל שלב:
השרת, המסד, הממשק, בדיקות היחידה והאינטגרציה, ה-CI, ההרצה ב-Docker והתיעוד.
הכללים שהנחו אותו נמצאים ב-[CLAUDE.md](CLAUDE.md).

**איפה נדרש תיקון:** כל תיקון מתועד ב-[docs/FIXES.md](docs/FIXES.md) עם הדרך שבה נמצא. בקצרה:
- **בבנייה** (תיקונים 1-3): התאמות לגרסאות של .NET 10 ו-xUnit v3.
- **בסקירת קוד** (4, 5, 7): הגדרות שנקראו מוקדם מדי, נתיב API לא קיים שהחזיר את דף ה-React, ותאריך שהושווה ב-UTC במקום בציריך.
- **בבדיקה בדפדפן** (8): רענון באמצע שליחה הציג "לא נשמר" כשההחתמה כבר נשמרה. זה הבאג המשמעותי ביותר שנמצא.
- **ב-CI** (9, 10, 12): דגל לא נתמך בפקודת הבדיקות, בדיקה שהזיזה שעון מזויף אחורה, וגרסת Playwright ישנה מדי לשרת של GitHub.
- **בבדיקת ההתאמה למטלה** (11): כניסה שמוקדמת מסוף המשמרת הקודמת יכלה ליצור משמרות חופפות.
- **החלטה של המשתמש:** לא לכבות את Smart App Control. לכן הבדיקות רצות ב-GitHub Actions, וההדגמה רצה ב-Docker.

**מה נבדק בפועל:** הפירוט המלא, כולל מה לא נבדק, נמצא ב-[docs/TESTING.md](docs/TESTING.md).

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
