# Plugin Builder Script
# Auto-detects and builds plugins or the server for CMZDedicatedSteamServer

$scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Path
$serverPath = Join-Path $scriptPath "CMZDedicatedSteamServer"
# Runtime plugin root inside the built server output (Build\CMZDedicatedSteamServer\Plugins)
$serverPluginsPath = Join-Path $scriptPath "Build\CMZDedicatedSteamServer\Plugins"

# Function to get available projects dynamically
function Get-Projects {
    $projects = @{}
    $index = 1

    # Scan shared plugins folder for .csproj files
    $pluginsDir = Join-Path $scriptPath "Shared\Plugins"
    if (Test-Path $pluginsDir) {
        $pluginProjects = Get-ChildItem -Path $pluginsDir -Filter "*.csproj" -Recurse
        foreach ($proj in $pluginProjects) {
            $pluginName = $proj.Directory.Name
            $relativePath = $proj.Directory.FullName.Substring($scriptPath.Length + 1).Replace('\', '/')
            $projects[$index.ToString()] = @{
                Name = $pluginName
                Path = $relativePath
                Project = $proj.Name
                IsServer = $false
            }
            $index++
        }
    }

    # Add server project
    $serverProj = Join-Path $serverPath "CMZDedicatedSteamServer.csproj"
    if (Test-Path $serverProj) {
        $projects[$index.ToString()] = @{
            Name = "CMZDedicatedSteamServer"
            Path = "CMZDedicatedSteamServer"
            Project = "CMZDedicatedSteamServer.csproj"
            IsServer = $true
        }
        $index++
    }

    return $projects
}

do {
    Clear-Host
    Write-Host "====================================" -ForegroundColor Cyan
    Write-Host "  CMZDedicatedSteamServer Builder" -ForegroundColor Cyan
    Write-Host "====================================" -ForegroundColor Cyan
    Write-Host ""

    $projects = Get-Projects

    if ($projects.Count -eq 0) {
        Write-Host "No projects found!" -ForegroundColor Red
        Write-Host "Press 'A' then Enter to continue..." -ForegroundColor Gray
        $keyInput = Read-Host
        if ($keyInput -ne 'A' -and $keyInput -ne 'a') {
            exit 0
        }
        continue
    }

    Write-Host "Available projects:" -ForegroundColor Yellow
    foreach ($key in $projects.Keys | Sort-Object { [int]$_ }) {
        $proj = $projects[$key]
        if ($proj.IsServer) {
            Write-Host "$key. $($proj.Name) (Server)" -ForegroundColor Cyan
        } else {
            Write-Host "$key. $($proj.Name)" -ForegroundColor White
        }
    }
    $exitKey = ($projects.Keys | Sort-Object { [int]$_ } | Select-Object -Last 1)
    $nextKey = [int]$exitKey + 1
    Write-Host "$nextKey. Exit" -ForegroundColor White
    Write-Host ""

    $selection = Read-Host "Select project to build"

    if ($selection -eq $nextKey.ToString()) {
        Write-Host "Exiting..." -ForegroundColor Gray
        exit 0
    }

    if (-not $projects.ContainsKey($selection)) {
        Write-Host "Invalid selection." -ForegroundColor Red
        Write-Host "Press 'A' then Enter to continue..." -ForegroundColor Gray
        $keyInput = Read-Host
        if ($keyInput -ne 'A' -and $keyInput -ne 'a') {
            Write-Host "Exiting..." -ForegroundColor Gray
            exit 0
        }
        continue
    }

    $project = $projects[$selection]
    $projectPath = Join-Path $scriptPath $project.Path

    Write-Host ""
    Write-Host "Building $($project.Name)..." -ForegroundColor Green
    Write-Host ""

    Push-Location $projectPath
    try {
        # Restore
        $restoreOutput = dotnet restore $project.Project 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Restore failed." -ForegroundColor Red
            $restoreOutput | Out-String | Write-Host
            Write-Host "Press 'A' then Enter to continue..." -ForegroundColor Gray
            $keyInput = Read-Host
            if ($keyInput -ne 'A' -and $keyInput -ne 'a') {
                Write-Host "Exiting..." -ForegroundColor Gray
                exit 0
            }
            continue
        }

        # Build
        $buildOutput = dotnet build $project.Project --configuration Release 2>&1
        $buildString = $buildOutput | Out-String

        if ($LASTEXITCODE -ne 0) {
            Write-Host "Build failed." -ForegroundColor Red
            $buildString | Write-Host
            Write-Host "Press 'A' then Enter to continue..." -ForegroundColor Gray
            $keyInput = Read-Host
            if ($keyInput -ne 'A' -and $keyInput -ne 'a') {
                Write-Host "Exiting..." -ForegroundColor Gray
                exit 0
            }
            continue
        }

        Write-Host ""
        Write-Host "Build successful!" -ForegroundColor Green

        if ($project.IsServer) {
            $outputPath = Join-Path $scriptPath "Build\CMZDedicatedSteamServer"
            Write-Host "Server built to: $outputPath" -ForegroundColor Gray
        } else {
            # Copy DLL to server plugins directory
            $pluginOutputDir = Join-Path $projectPath "bin\Release"
            if (Test-Path $pluginOutputDir) {
                $dllFiles = Get-ChildItem -Path $pluginOutputDir -Filter "$($project.Name).dll" -Recurse
                foreach ($dll in $dllFiles) {
                    $destPath = Join-Path $serverPluginsPath $dll.Name
                    Copy-Item $dll.FullName $destPath -Force
                    Write-Host "DLL copied to: $destPath" -ForegroundColor Gray
                }
            }

            # Copy any config files (.ini) from plugin directory to server plugin config directory
            $configDir = Join-Path $serverPluginsPath $project.Name
            if (-not (Test-Path $configDir)) {
                New-Item -ItemType Directory -Path $configDir -Force | Out-Null
            }

            $configFiles = Get-ChildItem -Path $projectPath -Filter "*.ini" -ErrorAction SilentlyContinue
            foreach ($config in $configFiles) {
                $destPath = Join-Path $configDir $config.Name
                Copy-Item $config.FullName $destPath -Force
                Write-Host "Config file copied to: $destPath" -ForegroundColor Gray
            }
        }

        # Check for warnings (only actual compiler warnings like "warning CSXXXX")
        $warnings = $buildOutput | Select-String "warning CS"
        if ($warnings) {
            Write-Host ""
            Write-Host "Warnings found: $($warnings.Count)" -ForegroundColor Yellow
            $warnings | ForEach-Object { Write-Host $_.Line.Trim() -ForegroundColor DarkYellow }
        }

        # Check for errors (only actual compiler errors like "error CSXXXX")
        $errors = $buildOutput | Select-String "error CS"
        if ($errors) {
            Write-Host ""
            Write-Host "Errors found: $($errors.Count)" -ForegroundColor Red
            $errors | ForEach-Object { Write-Host $_.Line.Trim() -ForegroundColor DarkRed }
        }

        if (-not $warnings -and -not $errors) {
            Write-Host ""
            Write-Host "No errors or warnings." -ForegroundColor Green
        }
    }
    finally {
        Pop-Location
    }

    Write-Host ""
    Write-Host "Press 'A' then Enter to continue..." -ForegroundColor Gray
    $keyInput = Read-Host
    if ($keyInput -ne 'A' -and $keyInput -ne 'a') {
        Write-Host "Exiting..." -ForegroundColor Gray
        exit 0
    }
} while ($true)
