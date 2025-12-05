# All-In-One Merger: Combines Funscripts and Videos
# Right-click and "Run with PowerShell"

# --- CONFIGURATION ---
$inputFolder   = ".\Input"
$tempFolder    = ".\TempTS"
$fileList      = "filelist.txt"
$maxThreads    = 4  # Number of videos to encode simultaneously
$targetRes     = "1920:1080" # Target resolution for standardization

# Check for FFmpeg/FFprobe availability
if (-not (Get-Command "ffmpeg" -ErrorAction SilentlyContinue) -or -not (Get-Command "ffprobe" -ErrorAction SilentlyContinue)) {
    Write-Host "Error: FFmpeg or FFprobe not found in PATH." -ForegroundColor Red
    Write-Host "Please install FFmpeg and add it to your system environment variables."
    Read-Host "Press Enter to exit..."
    exit
}

# Prompt for Output Name
Write-Host "========================================================" -ForegroundColor Cyan
Write-Host " Step 0: Configuration" -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan
$OutputName = Read-Host "Enter the name for the merged file (Default: MergedScript)"
if ([string]::IsNullOrWhiteSpace($OutputName)) { $OutputName = "MergedScript" }

$outputScript = "$OutputName.funscript"
$outputVideo  = "$OutputName.mp4"

# Check Input Folder
if (-not (Test-Path -LiteralPath $inputFolder)) {
    Write-Host "Folder 'Input' not found. Creating it..." -ForegroundColor Yellow
    New-Item -ItemType Directory -Path $inputFolder | Out-Null
    Write-Host "Please put your files in the 'Input' folder and run this again."
    Read-Host "Press Enter to exit..."
    exit
}

# Get Files (Sorted Alphabetically to ensure sync)
$scriptFiles = Get-ChildItem -LiteralPath $inputFolder -Filter *.funscript | Sort-Object Name
$videoFiles  = Get-ChildItem -LiteralPath $inputFolder -Filter *.mp4 | Sort-Object Name

# ========================================================
#  STEP 1: MERGE FUNSCRIPTS
# ========================================================
Write-Host "`n========================================================" -ForegroundColor Cyan
Write-Host " Step 1: Merging Funscripts" -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan

if ($scriptFiles.Count -eq 0) {
    Write-Host "No .funscript files found. Skipping script merge." -ForegroundColor Yellow
} else {
    $globalRootActions = New-Object System.Collections.Generic.List[PSCustomObject]
    $globalBookmarks   = New-Object System.Collections.Generic.List[PSCustomObject]
    $globalAuxAxes     = @{}
    $currentOffset     = 0
    $processedFiles    = New-Object System.Collections.Generic.HashSet[string]
    
    # Extensions to check for duration
    $vidExts = @(".mp4", ".mkv", ".avi", ".webm", ".m4v", ".ts")
    $i = 0

    foreach ($file in $scriptFiles) {
        $i++
        $percent = [int](($i / $scriptFiles.Count) * 100)
        Write-Progress -Activity "Merging Scripts" -Status "Processing $($file.Name)" -PercentComplete $percent

        if ($processedFiles.Contains($file.FullName)) { continue }
        Write-Host "Processing Scene: $($file.BaseName)" -NoNewline
        $processedFiles.Add($file.FullName) | Out-Null

        try {
            # A. Detect Video Duration
            $videoDurationMs = 0
            $videoFound = $false
            foreach ($ext in $vidExts) {
                $testPath = Join-Path $inputFolder ($file.BaseName + $ext)
                if (Test-Path -LiteralPath $testPath) {
                    $durString = ffprobe -v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 "$testPath" 2>&1
                    if ($durString) {
                        $durSec = [double]::Parse($durString.Trim(), [System.Globalization.CultureInfo]::InvariantCulture)
                        $videoDurationMs = [int]($durSec * 1000)
                        $videoFound = $true
                        Write-Host " -> Video: $($videoDurationMs)ms" -ForegroundColor Green -NoNewline
                    }
                    break
                }
            }
            if (-not $videoFound) { Write-Host " -> No Video" -ForegroundColor Yellow -NoNewline }

            # B. Read Main Script
            $mainContent = Get-Content -LiteralPath $file.FullName | Out-String
            $mainJson = $mainContent | ConvertFrom-Json
            $maxTimeInScene = 0
            
            # Root Actions
            if ($mainJson.actions) {
                foreach ($action in $mainJson.actions) {
                    $newTime = [int]$action.at + [int]$currentOffset
                    $globalRootActions.Add([PSCustomObject]@{ at = $newTime; pos = $action.pos })
                    if ($action.at -gt $maxTimeInScene) { $maxTimeInScene = $action.at }
                }
            }
            # Embedded Axes
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
            # Chapters
            $hasChapters = $false
            if ($mainJson.bookmarks) {
                $hasChapters = $true
                foreach ($bk in $mainJson.bookmarks) {
                    $bkTimeRaw = if ($null -ne $bk.time) { $bk.time } else { $bk.at }
                    $globalBookmarks.Add([Ordered]@{ name = $bk.name; time = ([int]$bkTimeRaw + [int]$currentOffset) })
                }
                Write-Host " -> Chapters Imported" -ForegroundColor Magenta -NoNewline
            }
            if (-not $hasChapters) {
                $globalBookmarks.Add([Ordered]@{ name = $file.BaseName; time = [int]$currentOffset })
                Write-Host " -> Auto-Chapter Created" -ForegroundColor DarkMagenta -NoNewline
            }

            # C. Process Sibling Files
            $pattern = "$([regex]::Escape($file.BaseName))\.(.+)\.funscript$"
            $siblings = Get-ChildItem -LiteralPath $inputFolder -Filter "$($file.BaseName).*.funscript"
            foreach ($sibling in $siblings) {
                if ($sibling.FullName -ne $file.FullName -and -not $processedFiles.Contains($sibling.FullName)) {
                    if ($sibling.Name -match $pattern) {
                        $axisName = $matches[1]
                        Write-Host "`n    + Merging Axis: [$axisName]" -ForegroundColor Cyan -NoNewline
                        $processedFiles.Add($sibling.FullName) | Out-Null
                        $subJson = Get-Content -LiteralPath $sibling.FullName | Out-String | ConvertFrom-Json
                        
                        if (-not $globalAuxAxes.ContainsKey($axisName)) { $globalAuxAxes[$axisName] = New-Object System.Collections.Generic.List[PSCustomObject] }
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

            Write-Host "" # Newline
            
            # D. Update Offset
            if ($videoFound -and $videoDurationMs -gt 0) { $currentOffset += $videoDurationMs }
            elseif ($mainJson.metadata -and $mainJson.metadata.duration) {
                $metaDur = [int]($mainJson.metadata.duration * 1000)
                if ($metaDur -gt $maxTimeInScene) { $currentOffset += $metaDur } else { $currentOffset += $maxTimeInScene }
            } else { $currentOffset += $maxTimeInScene }
        } catch {
            Write-Host "`nError processing $($file.Name): $($_.Exception.Message)" -ForegroundColor Red
        }
    }
    Write-Progress -Activity "Merging Scripts" -Completed

    # Save Funscript
    $finalObj = [Ordered]@{
        version = "1.0"; inverted = $false; range = 100
        actions = $globalRootActions; bookmarks = $globalBookmarks; axes = @()
    }
    foreach ($key in $globalAuxAxes.Keys) {
        $finalObj.axes += [Ordered]@{ id = $key; actions = $globalAuxAxes[$key] }
    }
    
    $finalObj | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $outputScript -Encoding UTF8
    Write-Host "Success! Saved script to $outputScript" -ForegroundColor Green
}

# ========================================================
#  STEP 2: MERGE VIDEOS (FFMPEG - PARALLEL)
# ========================================================
Write-Host "`n========================================================" -ForegroundColor Cyan
Write-Host " Step 2: Merging Videos (Parallel x$maxThreads)" -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan

if ($videoFiles.Count -eq 0) {
    Write-Host "No .mp4 files found in Input. Skipping video merge." -ForegroundColor Yellow
} else {
    # 1. Setup Folders
    if (Test-Path -LiteralPath $fileList) { Remove-Item -LiteralPath $fileList -Force }
    if (-not (Test-Path -LiteralPath $tempFolder)) { New-Item -ItemType Directory -Path $tempFolder | Out-Null }

    # 2. Normalize Inputs (Batch Processing)
    $runningJobs = @()
    $total = $videoFiles.Count
    $completed = 0

    foreach ($vid in $videoFiles) {
        $tsName = "$($vid.BaseName).ts"
        $tsPath = Join-Path $tempFolder $tsName
        
        # Add to filelist immediately (so the list order matches input order)
        # Escape single quotes in filename just in case
        $escapedPath = "TempTS\$tsName".Replace("'", "'\''")
        "file '$escapedPath'" | Out-File -FilePath $fileList -Append -Encoding ascii

        # FFmpeg Args: Standardize resolution + Safe TS conversion
        # We add 'scale' filter to force 1080p and black bars to prevent concat errors on mixed content
        $args = @(
            "-hide_banner", "-loglevel", "error",
            "-i", $vid.FullName,
            "-c:v", "libx264", "-crf", "23", "-preset", "fast",
            "-vf", "scale=$targetRes:force_original_aspect_ratio=decrease,pad=$targetRes:(ow-iw)/2:(oh-ih)/2",
            "-c:a", "aac", "-b:a", "192k",
            "-ac", "2", "-ar", "48000",
            "-af", "aresample=async=1",
            "-bsf:v", "h264_mp4toannexb",
            "-f", "mpegts",
            "-muxdelay", "0",
            "-y",
            $tsPath
        )

        # Wait if we hit max threads
        while ($runningJobs.Count -ge $maxThreads) {
            $finished = $runningJobs | Where-Object { $_.HasExited }
            foreach ($job in $finished) {
                $completed++
                $percent = [int](($completed / $total) * 100)
                Write-Progress -Activity "Encoding Videos" -Status "Processing batch... ($completed/$total)" -PercentComplete $percent
            }
            $runningJobs = $runningJobs | Where-Object { -not $_.HasExited }
            Start-Sleep -Milliseconds 200
        }

        # Start Process
        Write-Host "Queueing: $($vid.Name)"
        $runningJobs += Start-Process -FilePath "ffmpeg" -ArgumentList $args -NoNewWindow -PassThru
    }

    # Wait for remaining jobs
    while ($runningJobs.Count -gt 0) {
        $finished = $runningJobs | Where-Object { $_.HasExited }
        foreach ($job in $finished) {
            $completed++
            $percent = [int](($completed / $total) * 100)
            Write-Progress -Activity "Encoding Videos" -Status "Finishing last batch... ($completed/$total)" -PercentComplete $percent
        }
        $runningJobs = $runningJobs | Where-Object { -not $_.HasExited }
        Start-Sleep -Milliseconds 200
    }
    Write-Progress -Activity "Encoding Videos" -Completed

    # 3. Concatenate
    if (Test-Path -LiteralPath $fileList) {
        Write-Host "Concatenating files..."
        $concatArgs = @(
            "-hide_banner",
            "-f", "concat",
            "-safe", "0",
            "-i", $fileList,
            "-c", "copy",
            "-bsf:a", "aac_adtstoasc",
            "-movflags", "+faststart",
            "-y",
            $outputVideo
        )
        Start-Process -FilePath "ffmpeg" -ArgumentList $concatArgs -Wait -NoNewWindow
        
        Write-Host "Video merge complete! Output: $outputVideo" -ForegroundColor Green
        
        # Cleanup
        Remove-Item -LiteralPath $tempFolder -Recurse -Force
        Remove-Item -LiteralPath $fileList -Force
    } else {
        Write-Host "No files were successfully processed." -ForegroundColor Red
    }
}

Write-Host "`nAll operations complete." -ForegroundColor Cyan
Read-Host "Press Enter to close..."