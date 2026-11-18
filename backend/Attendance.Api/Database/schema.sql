-- Attendance clock schema. Idempotent: safe to run again without deleting data.
-- Run by: dotnet run --project backend/Attendance.Api -- --init-db
-- All times are stored as UTC in datetimeoffset(7) with a +00:00 offset (enforced by a CHECK).

IF OBJECT_ID(N'dbo.Employees', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Employees (
        Id          int            NOT NULL CONSTRAINT PK_Employees PRIMARY KEY,
        FullName    nvarchar(100)  NOT NULL,
        IsActive    bit            NOT NULL CONSTRAINT DF_Employees_IsActive DEFAULT (1)
    );
END;

IF OBJECT_ID(N'dbo.Shifts', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Shifts (
        Id           uniqueidentifier   NOT NULL CONSTRAINT PK_Shifts PRIMARY KEY NONCLUSTERED,
        EmployeeId   int                NOT NULL CONSTRAINT FK_Shifts_Employees REFERENCES dbo.Employees(Id),
        StartUtc     datetimeoffset(7)  NOT NULL,
        EndUtc       datetimeoffset(7)  NULL,
        StartSource  varchar(40)        NOT NULL,
        EndSource    varchar(40)        NULL,
        CONSTRAINT CK_Shifts_Order      CHECK (EndUtc IS NULL OR EndUtc >= StartUtc),
        CONSTRAINT CK_Shifts_StartIsUtc CHECK (DATEPART(TZOFFSET, StartUtc) = 0),
        CONSTRAINT CK_Shifts_EndIsUtc   CHECK (EndUtc IS NULL OR DATEPART(TZOFFSET, EndUtc) = 0),
        CONSTRAINT CK_Shifts_EndSource  CHECK ((EndUtc IS NULL AND EndSource IS NULL) OR (EndUtc IS NOT NULL AND EndSource IS NOT NULL))
    );

    -- Clustered by employee and time: fast history and range queries.
    CREATE CLUSTERED INDEX CX_Shifts_Employee_Start ON dbo.Shifts(EmployeeId, StartUtc);
    -- At most one open shift per employee, enforced by the database even if the code has a bug.
    CREATE UNIQUE INDEX UX_Shifts_OneOpenPerEmployee ON dbo.Shifts(EmployeeId) WHERE EndUtc IS NULL;
    -- For the report: shifts overlapping a time range.
    CREATE INDEX IX_Shifts_Start ON dbo.Shifts(StartUtc) INCLUDE (EndUtc);
END;

IF OBJECT_ID(N'dbo.Punches', N'U') IS NULL
BEGIN
    -- An immutable log of every punch accepted. The key (EmployeeId, RequestId) provides idempotency.
    CREATE TABLE dbo.Punches (
        EmployeeId           int                NOT NULL CONSTRAINT FK_Punches_Employees REFERENCES dbo.Employees(Id),
        RequestId            uniqueidentifier   NOT NULL,
        ShiftId              uniqueidentifier   NOT NULL CONSTRAINT FK_Punches_Shifts REFERENCES dbo.Shifts(Id),
        Action               varchar(3)         NOT NULL CONSTRAINT CK_Punches_Action CHECK (Action IN ('in', 'out')),
        AtUtc                datetimeoffset(7)  NOT NULL CONSTRAINT CK_Punches_AtIsUtc CHECK (DATEPART(TZOFFSET, AtUtc) = 0),
        TimeSource           varchar(40)        NOT NULL,
        ZurichOffsetMinutes  smallint           NOT NULL,
        -- AtUtc is the punch time (from the external service). RecordedAtUtc is the SQL Server clock at the moment the row
        -- was written: audit only, never displayed or used in calculations. The gap between them = how long the request took.
        RecordedAtUtc       datetimeoffset(7)  NOT NULL CONSTRAINT DF_Punches_RecordedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_Punches PRIMARY KEY (EmployeeId, RequestId)
    );

    -- Every shift has at most one clock-in and at most one clock-out.
    CREATE UNIQUE INDEX UX_Punches_ShiftAction ON dbo.Punches(ShiftId, Action);
END;

-- Sample employees (only if they do not exist).
IF NOT EXISTS (SELECT 1 FROM dbo.Employees WHERE Id = 1) INSERT INTO dbo.Employees (Id, FullName) VALUES (1, N'נועה כהן');
IF NOT EXISTS (SELECT 1 FROM dbo.Employees WHERE Id = 2) INSERT INTO dbo.Employees (Id, FullName) VALUES (2, N'יוסי לוי');
IF NOT EXISTS (SELECT 1 FROM dbo.Employees WHERE Id = 3) INSERT INTO dbo.Employees (Id, FullName) VALUES (3, N'מאיה פרץ');
