/*
    The read-only login the sidecar connects with, plus a resource limit so it can never compete with the
    application it is reading from.

    Run against the source database, once, with an account that has securityadmin (or sysadmin).

    Two separate concerns, deliberately not merged:
      1. Permissions — what the login is allowed to see. Scoped to the journal and the master-data tables the
         analysis needs, everything else denied. This is what makes "read only" a property of the account rather
         than a promise in a config file.
      2. Resource Governor — how much of the machine it may take. The instance already has pools for this; the
         sidecar is hung into the reporting pool rather than given one of its own. That part is commented out and
         run by hand, because changing the classifier affects every login on the server.

    Set the password before running. It goes into the source connection string, which is kept as a secret and
    nowhere else — not into a file in a repository, not into a ticket.
*/

-- Password and database come from the command line and have NO default here on purpose: a :setvar in the script
-- overrides -v, so a default would quietly win over whatever the operator passed and the script would run against
-- the wrong database with the wrong password. Undefined variables make sqlcmd stop, which is the behaviour we want.
--
--   sqlcmd -S <host> -U <admin> -P <pw> -C \
--          -v LoginPassword="…" -v DatabaseName="<database>" \
--          -i scripts/sql/create-readonly-login.sql
:setvar LoginName "process_analyzer_ro"

USE master;
GO

-- ============================================================================================================
-- 1. Login and user
-- ============================================================================================================

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = '$(LoginName)')
BEGIN
    CREATE LOGIN [$(LoginName)]
        WITH PASSWORD = '$(LoginPassword)',
             CHECK_POLICY = ON,
             DEFAULT_DATABASE = [$(DatabaseName)];
    PRINT 'Login created.';
END
ELSE
    PRINT 'Login already exists - password unchanged.';
GO

USE [$(DatabaseName)];
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = '$(LoginName)')
BEGIN
    CREATE USER [$(LoginName)] FOR LOGIN [$(LoginName)];
    PRINT 'Database user created.';
END
GO

-- ============================================================================================================
-- 2. Permissions: the journal, and the master data that makes it readable
-- ============================================================================================================
-- Table-level grants rather than db_datareader. db_datareader would open every table in the database, including
-- payroll and personnel data the analysis has no business reading, and a future table would be included
-- automatically without anybody deciding so.

-- Granted per table and only where the table exists: the journal is created by a migration that has not shipped
-- yet, and an unconditional GRANT on a missing table aborts the whole script. Re-run this file after the deploy to
-- pick up whatever was still missing — every statement here is idempotent.
DECLARE @tables TABLE (name sysname);
INSERT INTO @tables (name) VALUES
    -- the journal itself
    ('dbo.BusinessEvents'), ('dbo.BusinessEventObjects'),
    -- the directory: who exists and which group they act in, so the analysis speaks in roles rather than pseudonyms
    ('dbo.AspNetUsers'), ('dbo.UserGroups'), ('dbo.UserGroupMembers'),
    -- the business calendar, which decides what "two hours" means in every duration the product reports
    ('dbo.HolidayCalendarEntries'), ('dbo.WorktimeCalendarEntries'),
    -- the views people saved for themselves: the only place that says who somebody IS rather than what they did.
    -- Many people use the same module for completely different work, and a department is not a role — the columns and
    -- filters somebody configured are what tells them apart.
    --
    -- NOTE on what is read: the Data column of this table also holds the filter VALUES a person typed, which can be
    -- customer names or licence plates. SavedViewSync drops them on read and stores property NAMES only, because the
    -- name answers the role question and the value would import customer data for no analytical gain. The grant is
    -- wider than the use on purpose — SQL Server cannot grant half a column — so the restraint lives in the reader and
    -- is asserted by a test.
    ('dbo.ApplicationViews');

DECLARE @name sysname, @sql nvarchar(400), @missing nvarchar(2000) = N'';
DECLARE cur CURSOR LOCAL FAST_FORWARD FOR SELECT name FROM @tables;
OPEN cur;
FETCH NEXT FROM cur INTO @name;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF OBJECT_ID(@name) IS NULL
        SET @missing = @missing + @name + N' ';
    ELSE
    BEGIN
        SET @sql = N'GRANT SELECT ON ' + @name + N' TO [$(LoginName)];';
        EXEC sp_executesql @sql;
    END
    FETCH NEXT FROM cur INTO @name;
END
CLOSE cur; DEALLOCATE cur;

IF @missing <> N''
    PRINT 'Not granted, table does not exist yet (re-run after the deploy): ' + @missing;
GO

-- Belt and braces: an explicit DENY beats a GRANT that somebody adds later by accident, including through a role.
DENY INSERT, UPDATE, DELETE, ALTER, EXECUTE TO [$(LoginName)];
GO

-- ============================================================================================================
-- 3. Resource Governor: hang the account into the pool that already exists for this kind of work
-- ============================================================================================================
-- This instance already governs itself: appPool (80/75) carries the application, biPool (10/20, low importance)
-- carries reporting, and dbo.fn_RGClassifier in master routes by login and program name. The sidecar is reporting
-- workload, so it belongs in biPool — a third pool would only fragment a decision somebody already made.
--
-- Run this section by hand, deliberately, and preferably off-peak. Two reasons:
--   * The classifier cannot be altered while it is assigned. It has to be detached, changed and re-attached, and in
--     that window every NEW session is classified into 'default' (20% CPU) until the re-attach lands. Existing
--     sessions keep their group.
--   * The classifier decides the fate of every login on the instance. A mistake here does not slow the sidecar
--     down, it throttles the application.
--
-- Verify first that the function still looks the way this script expects:
--
--     SELECT OBJECT_DEFINITION(OBJECT_ID('master.dbo.fn_RGClassifier'));
--
-- Read the existing definition first and extend it. The block below is a TEMPLATE: keep whatever rules the
-- instance already has and add one line for this login. Pasting it verbatim would delete somebody else's rules.

/*  --- run this block only after reading the paragraph above ---

USE master;
GO

ALTER RESOURCE GOVERNOR WITH (CLASSIFIER_FUNCTION = NULL);
ALTER RESOURCE GOVERNOR RECONFIGURE;
GO

CREATE OR ALTER FUNCTION dbo.fn_RGClassifier()
RETURNS sysname
WITH SCHEMABINDING
AS
BEGIN
    DECLARE @grp sysname = N'default';
    DECLARE @login sysname = SUSER_SNAME();

    -- Keep the rules the instance already has here.

    -- This login: reporting workload, so it belongs in whichever group the instance reserves for reporting. Matched
    -- on the login and not on the program name, because a program name is something anybody can set.
    IF @login = N'process_analyzer_ro' SET @grp = N'<reporting-group>';

    RETURN @grp;
END;
GO

ALTER RESOURCE GOVERNOR WITH (CLASSIFIER_FUNCTION = dbo.fn_RGClassifier);
ALTER RESOURCE GOVERNOR RECONFIGURE;
GO

-- Proof: connect as process_analyzer_ro and check where the session landed. Any other group means the
-- classifier did not take.
--
--     SELECT g.name FROM sys.dm_exec_sessions s
--     JOIN sys.resource_governor_workload_groups g ON g.group_id = s.group_id
--     WHERE s.login_name = 'process_analyzer_ro';

*/

-- ============================================================================================================
-- 4. Proof, not assumption
-- ============================================================================================================
USE [$(DatabaseName)];
GO

SELECT 'read allowed'  AS check_name,
       HAS_PERMS_BY_NAME('dbo.BusinessEvents', 'OBJECT', 'SELECT', NULL, NULL) AS should_be_1;

EXECUTE AS USER = '$(LoginName)';
SELECT 'write denied'  AS check_name,
       HAS_PERMS_BY_NAME('dbo.BusinessEvents', 'OBJECT', 'INSERT') AS should_be_0;
SELECT 'payroll hidden' AS check_name,
       HAS_PERMS_BY_NAME('dbo.WageCalculationEntries', 'OBJECT', 'SELECT') AS should_be_0;
REVERT;
GO

/*
    Rollback, if it ever has to go — remove the classifier branch the same way it was added, then the account:

        USE $(DatabaseName); DROP USER [process_analyzer_ro];
        USE master;  DROP LOGIN [process_analyzer_ro];

    Dropping the login alone is enough to stop the sidecar; the classifier branch then simply never matches.
*/
