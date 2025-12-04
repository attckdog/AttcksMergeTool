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
$files = Get-ChildItem -Path $inputFolder -Filter *.funscript | Sort-Object Name

if ($files.Count -eq 0) {
    Write-Host "No .funscript files found in '$inputFolder'." -ForegroundColor Red
    exit
}

Write-Host "Found $($files.Count) scripts. Merging using Video Duration..." -ForegroundColor Cyan

# --- GLOBAL STORAGE ---
$globalRootActions = New-Object System.Collections.Generic.List[PSCustomObject]
$globalAuxAxes = @{}
$currentOffset = 0

# Video extensions to look for
$videoExtensions = @(".mp4", ".mkv", ".avi", ".webm", ".m4v", ".ts")

# 3. Process files
foreach ($file in $files) {
    Write-Host "Processing: $($file.Name)" -NoNewline
    
    try {
        # --- A. DETECT VIDEO DURATION ---
        $videoDurationMs = 0
        $videoFound = $false

        foreach ($ext in $videoExtensions) {
            $testPath = Join-Path $inputFolder ($file.BaseName + $ext)
            if (Test-Path $testPath) {
                try {
                    # Run ffprobe to get duration in seconds
                    $durString = ffprobe -v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 "$testPath" 2>&1
                    
                    if ($durString) {
                        # Parse string to double, then to int milliseconds
                        # We use InvariantCulture to ensure the dot (.) is treated as decimal separator
                        $durSec = [double]::Parse($durString.Trim(), [System.Globalization.CultureInfo]::InvariantCulture)
                        $videoDurationMs = [int]($durSec * 1000)
                        $videoFound = $true
                        Write-Host " -> Found Video ($($videoDurationMs)ms)" -ForegroundColor Green
                    }
                }
                catch {
                    Write-Host " -> Error reading video: $_" -ForegroundColor Red
                }
                break # Stop looking for extensions if we found one
            }
        }

        if (-not $videoFound) {
            Write-Host " -> No matching video found (Falling back to script actions)" -ForegroundColor Yellow
        }

        # --- B. READ SCRIPT ---
        $jsonContent = Get-Content -LiteralPath $file.FullName | Out-String
        $json = $jsonContent | ConvertFrom-Json
        
        $maxTimeInFile = 0
        $hasActions = $false

        # --- C. PROCESS ROOT ACTIONS ---
        if ($json.actions) {
            $hasActions = $true
            foreach ($action in $json.actions) {
                $newTime = [int]$action.at + [int]$currentOffset
                
                $globalRootActions.Add([PSCustomObject]@{
                    at = $newTime
                    pos = $action.pos
                })

                if ($action.at -gt $maxTimeInFile) { $maxTimeInFile = $action.at }
            }
        }

        # --- D. PROCESS AUX AXES ---
        if ($json.axes) {
            $hasActions = $true
            foreach ($axisObj in $json.axes) {
                $axisId = $axisObj.id
                if (-not $globalAuxAxes.ContainsKey($axisId)) {
                    $globalAuxAxes[$axisId] = New-Object System.Collections.Generic.List[PSCustomObject]
                }
                foreach ($action in $axisObj.actions) {
                    $newTime = [int]$action.at + [int]$currentOffset
                    $globalAuxAxes[$axisId].Add([PSCustomObject]@{
                        at = $newTime
                        pos = $action.pos
                    })
                    if ($action.at -gt $maxTimeInFile) { $maxTimeInFile = $action.at }
                }
            }
        }

        # --- E. UPDATE OFFSET ---
        if ($hasActions) {
            # PRIORITY 1: Use actual Video Duration if found
            if ($videoFound -and $videoDurationMs -gt 0) {
                $currentOffset += $videoDurationMs
            } 
            # PRIORITY 2: Use Metadata if available
            elseif ($json.metadata -and $json.metadata.duration) {
                $metaDur = [int]($json.metadata.duration * 1000)
                if ($metaDur -gt $maxTimeInFile) {
                    $currentOffset += $metaDur
                } else {
                    $currentOffset += $maxTimeInFile
                }
            }
            # PRIORITY 3: Use last action timestamp
            else {
                $currentOffset += $maxTimeInFile
            }
        } 

    }
    catch {
        Write-Host "`nError reading $($file.Name): $($_.Exception.Message)" -ForegroundColor Red
    }
}

# 4. Construct Final JSON Object
$finalObj = [Ordered]@{
    version = "1.0"
    inverted = $false
    range = 100
    actions = $globalRootActions
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