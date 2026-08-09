# ВНИМАНИЕ: файл должен храниться в UTF-8 **с BOM** — Windows PowerShell 5.1
# читает файлы без BOM как ANSI, из-за чего кириллица и тире ломают парсер.
#
# Создаёт (пересоздаёт) демонстрационную базу PostgreSQL для WebDbViewer.
#
#   powershell -File tools\testdata\create_pg_demo.ps1
#   powershell -File tools\testdata\create_pg_demo.ps1 -Database webdbviewer_demo -Password 1 -Recreate
#
# База dbviewer_db (метахранилище приложения) не затрагивается.

param(
    [string] $PsqlPath = "C:\Program Files\PostgreSQL\18\bin\psql.exe",
    [string] $DbHost   = "localhost",
    [int]    $Port     = 5432,
    [string] $User     = "postgres",
    [string] $Password = "1",
    [string] $Database = "webdbviewer_demo",
    [switch] $Recreate
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $PsqlPath)) { throw "psql не найден: $PsqlPath" }

$env:PGPASSWORD       = $Password
$env:PGCLIENTENCODING = "UTF8"

$script = Join-Path $PSScriptRoot "pg_demo.sql"
if (-not (Test-Path $script)) { throw "Не найден скрипт: $script" }

$exists = & $PsqlPath -h $DbHost -p $Port -U $User -d postgres -q -A -t `
    -c "SELECT 1 FROM pg_database WHERE datname = '$Database'"

if ($exists -and $Recreate) {
    Write-Host "Удаляю существующую базу $Database ..."
    & $PsqlPath -h $DbHost -p $Port -U $User -d postgres -q `
        -c "DROP DATABASE IF EXISTS $Database WITH (FORCE)"
    if ($LASTEXITCODE -ne 0) { throw "DROP DATABASE завершился с кодом $LASTEXITCODE" }
    $exists = $null
}

if (-not $exists) {
    Write-Host "Создаю базу $Database ..."
    & $PsqlPath -h $DbHost -p $Port -U $User -d postgres -q `
        -c "CREATE DATABASE $Database ENCODING 'UTF8' TEMPLATE template0"
    if ($LASTEXITCODE -ne 0) { throw "CREATE DATABASE завершился с кодом $LASTEXITCODE" }
} else {
    Write-Host "База $Database уже существует — пересоздаю только демо-схемы."
}

Write-Host "Применяю $script ..."
& $PsqlPath -h $DbHost -p $Port -U $User -d $Database -v ON_ERROR_STOP=1 -f $script
if ($LASTEXITCODE -ne 0) { throw "Скрипт завершился с кодом $LASTEXITCODE" }

Write-Host "Готово. Строка подключения:"
Write-Host "  Host=$DbHost;Port=$Port;Database=$Database;Username=$User;Password=$Password"
