# Accept the name passed from the Batch file
param([string]$OutputName = "MergedScript")

$inputFolder = ".\Input"
$outputFile = "$OutputName.funscript"

# --- CHECK FOR FFPROBE ---
if (-not (Get-Command "ffprobe" -ErrorAction SilentlyContinue)) {
    Write-Host "Error: 'ffprobe' is not found in your PATH." -ForegroundColor Red
    Write-Host "Please install FFmpeg and ensure it is added to your system environment variables."
    exit
}

# 1. Check if Input folder exists
if (-not (Test-Path $inputFolder)) {
    Write-Host "Folder 'Input' not found. Creating it..." -ForegroundColor Yellow
    New-Item -ItemType Directory -Path $inputFolder | Out-Null
    Write-Host "Please put your .funscript AND matching video files in the 'Input' folder and run this again."
    exit
}

# 2. Get files sorted alphabetically
$allFiles = Get-ChildItem -Path $inputFolder -Filter *.funscript | Sort-Object Name

if ($allFiles.Count -eq 0) {
    Write-Host "No .funscript files found in '$inputFolder'." -ForegroundColor Red
    exit
}

Write-Host "Found $($allFiles.Count) scripts. Grouping by scene..." -ForegroundColor Cyan

# --- GLOBAL STORAGE ---
$globalRootActions = New-Object System.Collections.Generic.List[PSCustomObject]
$globalBookmarks   = New-Object System.Collections.Generic.List[PSCustomObject]
$globalAuxAxes     = @{}
$currentOffset     = 0

# Keep track of files we have already merged so we don't process them twice
$processedFiles = New-Object System.Collections.Generic.HashSet[string]

# Video extensions to look for
$videoExtensions = @(".mp4", ".mkv", ".avi", ".webm", ".m4v", ".ts")

# 3. Process files
foreach ($file in $allFiles) {
    # If we already processed this file as a "child" axis of another script, skip it
    if ($processedFiles.Contains($file.FullName)) {
        continue
    }

    Write-Host "Processing Scene: $($file.BaseName)" -NoNewline
    $processedFiles.Add($file.FullName) | Out-Null

    try {
        # --- A. DETECT VIDEO DURATION ---
        $videoDurationMs = 0
        $videoFound = $false

        foreach ($ext in $videoExtensions) {
            $testPath = Join-Path $inputFolder ($file.BaseName + $ext)
            if (Test-Path -LiteralPath $testPath) {
                try {
                    # Quotes around "$testPath" handle spaces and brackets for external commands
                    $durString = ffprobe -v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 "$testPath" 2>&1
                    if ($durString) {
                        $durSec = [double]::Parse($durString.Trim(), [System.Globalization.CultureInfo]::InvariantCulture)
                        $videoDurationMs = [int]($durSec * 1000)
                        $videoFound = $true
                        Write-Host " -> Video: $($videoDurationMs)ms" -ForegroundColor Green -NoNewline
                    }
                } catch {
                    Write-Host " -> Video Error" -ForegroundColor Red -NoNewline
                }
                break
            }
        }
        if (-not $videoFound) { Write-Host " -> No Video" -ForegroundColor Yellow -NoNewline }

        # --- B. PROCESS MAIN SCRIPT ---
        $mainContent = Get-Content -LiteralPath $file.FullName | Out-String
        $mainJson = $mainContent | ConvertFrom-Json
        $maxTimeInScene = 0
        
        # 1. Process Root Actions
        if ($mainJson.actions) {
            foreach ($action in $mainJson.actions) {
                $newTime = [int]$action.at + [int]$currentOffset
                $globalRootActions.Add([PSCustomObject]@{ at = $newTime; pos = $action.pos })
                if ($action.at -gt $maxTimeInScene) { $maxTimeInScene = $action.at }
            }
        }
        
        # 2. Process Embedded Axes
        if ($mainJson.axes) {
            foreach ($axisObj in $mainJson.axes) {
                $axId = $axisObj.id
                if (-not $globalAuxAxes.ContainsKey($axId)) { $globalAuxAxes[$axId] = New-Object System.Collections.Generic.List[PSCustomObject] }
                foreach ($action in $axisObj.actions) {
                    $newTime = [int]$action.at + [int]$currentOffset
                    $globalAuxAxes[$axId].Add([PSCustomObject]@{ at = $newTime; pos = $action.pos })
                    if ($action.at -gt $maxTimeInScene) { $maxTimeInScene = $action.at }
                }
            }
        }

        # 3. PROCESS CHAPTERS (BOOKMARKS)
        $hasChapters = $false
        if ($mainJson.bookmarks) {
            $hasChapters = $true
            foreach ($bk in $mainJson.bookmarks) {
                $bkTimeRaw = if ($null -ne $bk.time) { $bk.time } else { $bk.at }
                $bkTime = [int]$bkTimeRaw + [int]$currentOffset
                
                $globalBookmarks.Add([Ordered]@{
                    name = $bk.name
                    time = $bkTime
                })
            }
             Write-Host " -> Chapters Imported" -ForegroundColor Magenta -NoNewline
        }

        if (-not $hasChapters) {
            $globalBookmarks.Add([Ordered]@{
                name = $file.BaseName
                time = [int]$currentOffset
            })
             Write-Host " -> Auto-Chapter Created" -ForegroundColor DarkMagenta -NoNewline
        }

        # --- C. FIND AND PROCESS SIBLING FILES ---
        $pattern = "$([regex]::Escape($file.BaseName))\.(.+)\.funscript$"
        $siblings = Get-ChildItem -Path $inputFolder -Filter "$($file.BaseName).*.funscript"
        
        foreach ($sibling in $siblings) {
            if ($sibling.FullName -ne $file.FullName -and -not $processedFiles.Contains($sibling.FullName)) {
                if ($sibling.Name -match $pattern) {
                    $axisName = $matches[1]
                    Write-Host "`n    + Merging Axis: [$axisName]" -ForegroundColor Cyan -NoNewline
                    $processedFiles.Add($sibling.FullName) | Out-Null
                    
                    $subContent = Get-Content -LiteralPath $sibling.FullName | Out-String
                    $subJson = $subContent | ConvertFrom-Json
                    
                    if (-not $globalAuxAxes.ContainsKey($axisName)) {
                        $globalAuxAxes[$axisName] = New-Object System.Collections.Generic.List[PSCustomObject]
                    }

                    if ($subJson.actions) {
                        foreach ($action in $subJson.actions) {
                            $newTime = [int]$action.at + [int]$currentOffset
                            $globalAuxAxes[$axisName].Add([PSCustomObject]@{ at = $newTime; pos = $action.pos })
                            if ($action.at -gt $maxTimeInScene) { $maxTimeInScene = $action.at }
                        }
                    }
                }
            }
        }

        Write-Host "" # New line

        # --- D. UPDATE OFFSET FOR NEXT SCENE ---
        if ($videoFound -and $videoDurationMs -gt 0) {
            $currentOffset += $videoDurationMs
        } elseif ($mainJson.metadata -and $mainJson.metadata.duration) {
            $metaDur = [int]($mainJson.metadata.duration * 1000)
            if ($metaDur -gt $maxTimeInScene) { $currentOffset += $metaDur } 
            else { $currentOffset += $maxTimeInScene }
        } else {
            $currentOffset += $maxTimeInScene
        }

    }
    catch {
        Write-Host "`nError processing $($file.Name): $($_.Exception.Message)" -ForegroundColor Red
    }
}

# 4. Construct Final JSON Object
$finalObj = [Ordered]@{
    version = "1.0"
    inverted = $false
    range = 100
    actions = $globalRootActions
    bookmarks = $globalBookmarks
    axes = @()
}

foreach ($key in $globalAuxAxes.Keys) {
    $finalObj.axes += [Ordered]@{
        id = $key
        actions = $globalAuxAxes[$key]
    }
}

Write-Host "Saving to $outputFile..."
$finalObj | ConvertTo-Json -Depth 10 | Set-Content $outputFile -Encoding UTF8
Write-Host "Success! Saved to $outputFile" -ForegroundColor Green