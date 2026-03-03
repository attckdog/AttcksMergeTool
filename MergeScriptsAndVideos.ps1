# All-In-One Merger: Combines Funscripts and Videos
# Right-click and "Run with PowerShell"

# --- CONFIGURATION ---
$inputFolder   = ".\Input"
$tempFolder    = ".\TempTS"
$fileList      = "filelist.txt"
$metaFile      = "ffmetadata.txt"
$maxThreads    = 3            # Capped at 3 to prevent consumer NVIDIA GPU NVENC session limits
$targetRes     = "1920:1080"  
$targetFps     = 60           
$useNvenc      = $true        # Set to $true to use NVIDIA hardware acceleration
$useAv1        = $true        # Set to $true to use AV1 (Requires RTX 40-series for NVENC)

# --- AXIS MAPPING FOR MULTIFUNPLAYER ---
# Translates filename/internal axis names to MultiFunPlayer standard IDs
$axisMap = @{
    "surge" = "L1"
    "sway"  = "L2"
    "twist" = "R0"
    "roll"  = "R1"
    "pitch" = "R2"
}

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
if ($useNvenc) {
    Write-Host "NVENC (Full Hardware Acceleration) is ENABLED." -ForegroundColor Green
} else {
    Write-Host "NVENC is DISABLED (Using CPU Encoding)." -ForegroundColor Yellow
}

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

# Get Files (Sorted Alphabetically)
$scriptFiles = Get-ChildItem -LiteralPath $inputFolder -Filter *.funscript | Sort-Object Name
$videoFiles  = Get-ChildItem -LiteralPath $inputFolder -Filter *.mp4 | Sort-Object Name

# ========================================================
#  STEP 1: MERGE FUNSCRIPTS & COLLECT METADATA
# ========================================================
Write-Host "`n========================================================" -ForegroundColor Cyan
Write-Host " Step 1: Merging Funscripts" -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan

$globalBookmarks = New-Object System.Collections.Generic.List[PSCustomObject]
$totalDurationMs = 0

# --- Metadata Collectors ---
$metaCreators   = New-Object System.Collections.Generic.HashSet[string]
$metaTags       = New-Object System.Collections.Generic.HashSet[string]
$metaPerformers = New-Object System.Collections.Generic.HashSet[string]
$metaLicenses   = New-Object System.Collections.Generic.HashSet[string]
$metaDesc       = New-Object System.Collections.Generic.List[string]
$metaNotes      = New-Object System.Collections.Generic.List[string]
$metaType       = "basic" 

if ($scriptFiles.Count -eq 0) {
    Write-Host "No .funscript files found. Skipping script merge." -ForegroundColor Yellow
} else {
    $globalRootActions = New-Object System.Collections.Generic.List[PSCustomObject]
    $globalAuxAxes     = @{}
    $currentOffset     = 0
    $processedFiles    = New-Object System.Collections.Generic.HashSet[string]
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
            
            # --- METADATA SCRAPING ---
            if ($mainJson.metadata) {
                if ($mainJson.metadata.creator) { $metaCreators.Add($mainJson.metadata.creator) | Out-Null }
                if ($mainJson.metadata.license) { $metaLicenses.Add($mainJson.metadata.license) | Out-Null }
                if ($mainJson.metadata.tags) { foreach ($t in $mainJson.metadata.tags) { $metaTags.Add($t) | Out-Null } }
                if ($mainJson.metadata.performers) { foreach ($p in $mainJson.metadata.performers) { $metaPerformers.Add($p) | Out-Null } }
                if ($mainJson.metadata.description) { $metaDesc.Add("[$($file.BaseName)]: $($mainJson.metadata.description)") }
                if ($mainJson.metadata.notes) { $metaNotes.Add("[$($file.BaseName)]: $($mainJson.metadata.notes)") }
            }

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
                $metaType = "multiaxis" 
                foreach ($axisObj in $mainJson.axes) {
                    $rawId = $axisObj.id.ToLower()
                    $axId = if ($axisMap.ContainsKey($rawId)) { $axisMap[$rawId] } else { $axisObj.id }
                    
                    if (-not $globalAuxAxes.ContainsKey($axId)) { $globalAuxAxes[$axId] = New-Object System.Collections.Generic.List[PSCustomObject] }
                    foreach ($action in $axisObj.actions) {
                        $newTime = [int]$action.at + [int]$currentOffset
                        $globalAuxAxes[$axId].Add([PSCustomObject]@{ at = $newTime; pos = $action.pos })
                        if ($action.at -gt $maxTimeInScene) { $maxTimeInScene = $action.at }
                    }
                }
            }
            
            # --- CHAPTERS ---
            $globalBookmarks.Add([Ordered]@{ name = $file.BaseName; time = [int]$currentOffset })
            
            if ($mainJson.bookmarks) {
                foreach ($bk in $mainJson.bookmarks) {
                    $bkTimeRaw = if ($null -ne $bk.time) { $bk.time } else { $bk.at }
                    if ($bkTimeRaw -eq 0 -and $bk.name -eq $file.BaseName) { continue }
                    $globalBookmarks.Add([Ordered]@{ name = $bk.name; time = ([int]$bkTimeRaw + [int]$currentOffset) })
                }
                Write-Host " -> Chapters Merged" -ForegroundColor Magenta -NoNewline
            } else {
                Write-Host " -> Main Chapter Added" -ForegroundColor DarkMagenta -NoNewline
            }

            # C. Process Sibling Files (Multi-Axis Support)
            $siblings = Get-ChildItem -LiteralPath $inputFolder -Filter "$($file.BaseName).*.funscript"
            foreach ($sibling in $siblings) {
                if ($sibling.FullName -ne $file.FullName -and -not $processedFiles.Contains($sibling.FullName)) {
                    $processedFiles.Add($sibling.FullName) | Out-Null
                    $subJson = Get-Content -LiteralPath $sibling.FullName | Out-String | ConvertFrom-Json
                    $metaType = "multiaxis"
                    
                    # 1. Map root actions to the axis name found in the filename
                    $pattern = "$([regex]::Escape($file.BaseName))\.(.+)\.funscript$"
                    if ($sibling.Name -match $pattern) {
                        $rawAxisName = $matches[1].ToLower()
                        $axisName = if ($axisMap.ContainsKey($rawAxisName)) { $axisMap[$rawAxisName] } else { $matches[1] }
                        
                        if ($subJson.actions) {
                            Write-Host "`n    + Merging Axis (from actions): [$($matches[1]) -> $axisName]" -ForegroundColor Cyan -NoNewline
                            if (-not $globalAuxAxes.ContainsKey($axisName)) { $globalAuxAxes[$axisName] = New-Object System.Collections.Generic.List[PSCustomObject] }
                            foreach ($action in $subJson.actions) {
                                $newTime = [int]$action.at + [int]$currentOffset
                                $globalAuxAxes[$axisName].Add([PSCustomObject]@{ at = $newTime; pos = $action.pos })
                                if ($action.at -gt $maxTimeInScene) { $maxTimeInScene = $action.at }
                            }
                        }
                    }

                    # 2. Map embedded axes from the sibling file natively
                    if ($subJson.axes) {
                        Write-Host "`n    + Merging Axes (from embedded):" -ForegroundColor Cyan -NoNewline
                        foreach ($axisObj in $subJson.axes) {
                            $rawId = $axisObj.id.ToLower()
                            $axId = if ($axisMap.ContainsKey($rawId)) { $axisMap[$rawId] } else { $axisObj.id }
                            
                            Write-Host " [$($axisObj.id) -> $axId]" -ForegroundColor Cyan -NoNewline
                            if (-not $globalAuxAxes.ContainsKey($axId)) { $globalAuxAxes[$axId] = New-Object System.Collections.Generic.List[PSCustomObject] }
                            foreach ($action in $axisObj.actions) {
                                $newTime = [int]$action.at + [int]$currentOffset
                                $globalAuxAxes[$axId].Add([PSCustomObject]@{ at = $newTime; pos = $action.pos })
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
    
    $totalDurationMs = $currentOffset
    Write-Progress -Activity "Merging Scripts" -Completed

    # --- BUILD FINAL METADATA OBJECT ---
    $finalMeta = [Ordered]@{
        bookmarks   = $globalBookmarks
        chapters    = $globalBookmarks
        creator     = ($metaCreators -join " + ")
        description = ($metaDesc -join "`n")
        duration    = [int]($totalDurationMs / 1000)
        license     = ($metaLicenses -join ", ")
        notes       = ($metaNotes -join "`n")
        performers  = $metaPerformers
        script_url  = ""
        tags        = $metaTags
        title       = $OutputName
        type        = $metaType
        video_url   = ""
    }

    $finalObj = [Ordered]@{
        version = "1.0"
        inverted = $false
        range = 100
        metadata = $finalMeta
        actions = $globalRootActions
        bookmarks = $globalBookmarks 
        axes = @()
    }
    foreach ($key in $globalAuxAxes.Keys) {
        $finalObj.axes += [Ordered]@{ id = $key; actions = $globalAuxAxes[$key] }
    }
    
    # Write JSON as UTF-8 without BOM (Ensures compatibility with all players)
    $jsonString = $finalObj | ConvertTo-Json -Depth 10
    $utf8NoBom = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText((Join-Path $PWD $outputScript), $jsonString, $utf8NoBom)
    Write-Host "Success! Saved script to $outputScript" -ForegroundColor Green
}

# ========================================================
#  STEP 2: PREPARE VIDEO CHAPTERS
# ========================================================
if ($globalBookmarks.Count -gt 0) {
    Write-Host "Generating Video Chapters metadata..." -ForegroundColor Cyan
    
    # Cap the final chapter by inserting an End-of-File marker
    $globalBookmarks.Add([Ordered]@{ name = "EOF"; time = $totalDurationMs })
    $sortedBk = $globalBookmarks | Sort-Object time

    $sb = New-Object System.Text.StringBuilder
    $sb.AppendLine(";FFMETADATA1") | Out-Null
    $sb.AppendLine("title=$OutputName") | Out-Null
    
    for ($k = 0; $k -lt ($sortedBk.Count - 1); $k++) {
        $start = [int]$sortedBk[$k].time
        $end   = [int]$sortedBk[$k+1].time

        if ($end -eq $start) { continue }
        if ($end -le $start) { $end = $start + 1000 }
        
        # Prevent the EOF marker itself from being written as a named chapter
        if ($sortedBk[$k].name -eq "EOF") { continue } 

        $sb.AppendLine("[CHAPTER]") | Out-Null
        $sb.AppendLine("TIMEBASE=1/1000") | Out-Null
        $sb.AppendLine("START=$start") | Out-Null
        $sb.AppendLine("END=$end") | Out-Null
        $sb.AppendLine("title=$($sortedBk[$k].name)") | Out-Null
    }
    
    $utf8NoBom = New-Object System.Text.UTF8Encoding $false
    $absMetaPath = Join-Path -Path $PWD -ChildPath $metaFile
    [System.IO.File]::WriteAllText($absMetaPath, $sb.ToString(), $utf8NoBom)
}

# ========================================================
#  STEP 3: MERGE VIDEOS (FFMPEG - PARALLEL)
# ========================================================
Write-Host "`n========================================================" -ForegroundColor Cyan
Write-Host " Step 3: Merging Videos (Parallel x$maxThreads)" -ForegroundColor Cyan
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
    $jobIndex = 0

    try {
        foreach ($vid in $videoFiles) {
            $jobIndex++

            # --- CODEC SELECTION LOGIC ---
            if ($useAv1) {
                $safeName = "{0:D4}.mkv" -f $jobIndex
                $tempFormat = "matroska"
                $bsfFilter = @() 

                if ($useNvenc) {
                    $inputParams    = @("-hwaccel", "cuda")
                    $encodingParams = @("-c:v", "av1_nvenc", "-rc", "vbr", "-cq", "30", "-preset", "p4")
                } else {
                    $inputParams    = @()
                    $encodingParams = @("-c:v", "libsvtav1", "-crf", "30", "-preset", "8")
                }
            } else {
                $safeName = "{0:D4}.ts" -f $jobIndex
                $tempFormat = "mpegts"
                $bsfFilter = @("-bsf:v", "h264_mp4toannexb")

                if ($useNvenc) {
                    $inputParams    = @("-hwaccel", "cuda")
                    $encodingParams = @("-c:v", "h264_nvenc", "-rc", "vbr", "-cq", "23", "-preset", "p4")
                } else {
                    $inputParams    = @()
                    $encodingParams = @("-c:v", "libx264", "-crf", "23", "-preset", "fast")
                }
            }

            # Safe absolute path for FFmpeg
            $tsPath = Join-Path $tempFolder $safeName
            $ffmpegSafePath = $tsPath.Replace('\', '/')
            "file '$ffmpegSafePath'" | Out-File -FilePath $fileList -Append -Encoding ascii

            $videoFilter = "scale=${targetRes}:force_original_aspect_ratio=decrease,pad=${targetRes}:(ow-iw)/2:(oh-ih)/2"

            $argsList = @("-hide_banner", "-loglevel", "error") + $inputParams + @(
                "-i", "`"$($vid.FullName)`""
            ) + $encodingParams + @(
                "-vf", $videoFilter,
                "-r", $targetFps,
                "-c:a", "aac", "-b:a", "192k",
                "-ac", "2", "-ar", "48000",
                "-af", "aresample=async=1"
            ) + $bsfFilter + @(
                "-f", $tempFormat,
                "-muxdelay", "0",
                "-y",
                $tsPath
            )

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

            Write-Host "Queueing: $($vid.Name) [AV1: $useAv1]"
            $runningJobs += Start-Process -FilePath "ffmpeg" -ArgumentList $argsList -NoNewWindow -PassThru
        }

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
    } finally {
        # Cleanup orphaned FFmpeg processes if script is aborted (Ctrl+C)
        $runningJobs | Where-Object { -not $_.HasExited } | ForEach-Object {
            Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
        }
    }

    # 3. Concatenate and Inject Chapters
    if (Test-Path -LiteralPath $fileList) {
        Write-Host "Concatenating files and embedding chapters..."
        
        $concatArgs = @(
            "-hide_banner",
            "-f", "concat",
            "-safe", "0",
            "-i", $fileList
        )

        if (Test-Path -LiteralPath $metaFile) {
            $concatArgs += @("-i", "`"$metaFile`"", "-map_metadata", "1")
        }

        $concatArgs += @(
            "-c", "copy",
            "-movflags", "+faststart",
            "-y",
            "`"$outputVideo`""
        )

        if (-not $useAv1) {
            $concatArgs += @("-bsf:a", "aac_adtstoasc")
        }

        Start-Process -FilePath "ffmpeg" -ArgumentList $concatArgs -Wait -NoNewWindow
        
        Write-Host "Video merge complete! Output: $outputVideo" -ForegroundColor Green
        
        # Cleanup
        Remove-Item -LiteralPath $tempFolder -Recurse -Force
        Remove-Item -LiteralPath $fileList -Force
        if (Test-Path -LiteralPath $metaFile) { Remove-Item -LiteralPath $metaFile -Force }
    } else {
        Write-Host "No files were successfully processed." -ForegroundColor Red
    }
}

Write-Host "`nAll operations complete." -ForegroundColor Cyan
Read-Host "Press Enter to close..."