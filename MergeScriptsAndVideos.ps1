# All-In-One Merger: Combines Funscripts and Videos
# Right-click and "Run with PowerShell"

# --- CONFIGURATION ---
$inputFolder   = ".\Input"
$tempFolder    = ".\TempTS"
$fileList      = "filelist.txt"
$metaFile      = "ffmetadata.txt"
$maxThreads    = 1            # Number of videos to encode simultaneously
$targetRes     = "1920:1080"  # Target resolution
$targetFps     = 60           # Target Framerate (60 is best for scripts)
$useNvenc      = $false       # Set to $true to use NVIDIA hardware acceleration

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
    Write-Host "NVENC is DISABLED (Using CPU Encoding). If you have an nVidia GPU enable this by editting the script." -ForegroundColor Yellow
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
                    $axId = $axisObj.id
                    if (-not $globalAuxAxes.ContainsKey($axId)) { $globalAuxAxes[$axId] = New-Object System.Collections.Generic.List[PSCustomObject] }
                    foreach ($action in $axisObj.actions) {
                        $newTime = [int]$action.at + [int]$currentOffset
                        $globalAuxAxes[$axId].Add([PSCustomObject]@{ at = $newTime; pos = $action.pos })
                        if ($action.at -gt $maxTimeInScene) { $maxTimeInScene = $action.at }
                    }
                }
            }
            
            # --- CHAPTERS ---
            # 1. ALWAYS add the Filename as a main chapter for this video
            $globalBookmarks.Add([Ordered]@{ name = $file.BaseName; time = [int]$currentOffset })
            
            # 2. Add internal bookmarks (if any)
            if ($mainJson.bookmarks) {
                foreach ($bk in $mainJson.bookmarks) {
                    $bkTimeRaw = if ($null -ne $bk.time) { $bk.time } else { $bk.at }
                    # Avoid adding duplicates if internal bookmark is at 0ms and named the same
                    if ($bkTimeRaw -eq 0 -and $bk.name -eq $file.BaseName) { continue }
                    
                    $globalBookmarks.Add([Ordered]@{ name = $bk.name; time = ([int]$bkTimeRaw + [int]$currentOffset) })
                }
                Write-Host " -> Chapters Merged" -ForegroundColor Magenta -NoNewline
            } else {
                Write-Host " -> Main Chapter Added" -ForegroundColor DarkMagenta -NoNewline
            }

            # C. Process Sibling Files
            $pattern = "$([regex]::Escape($file.BaseName))\.(.+)\.funscript$"
            $siblings = Get-ChildItem -LiteralPath $inputFolder -Filter "$($file.BaseName).*.funscript"
            foreach ($sibling in $siblings) {
                if ($sibling.FullName -ne $file.FullName -and -not $processedFiles.Contains($sibling.FullName)) {
                    if ($sibling.Name -match $pattern) {
                        $axisName = $matches[1]
                        Write-Host "`n    + Merging Axis: [$axisName]" -ForegroundColor Cyan -NoNewline
                        $metaType = "multiaxis"
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
    
    $finalObj | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $outputScript -Encoding UTF8
    Write-Host "Success! Saved script to $outputScript" -ForegroundColor Green
}

# ========================================================
#  STEP 2: PREPARE VIDEO CHAPTERS
# ========================================================
if ($globalBookmarks.Count -gt 0) {
    Write-Host "Generating Video Chapters metadata..." -ForegroundColor Cyan
    $sb = New-Object System.Text.StringBuilder
    $sb.AppendLine(";FFMETADATA1") | Out-Null
    $sb.AppendLine("title=$OutputName") | Out-Null
    
    # Sort bookmarks by time
    $sortedBk = $globalBookmarks | Sort-Object time

    # Loop to (Count - 1) skips the calculation for the final chapter
    for ($k = 0; $k -lt ($sortedBk.Count - 1); $k++) {
        $start = [int]$sortedBk[$k].time
        $end   = [int]$sortedBk[$k+1].time

        # CLEANUP: If Start == End, it means we have two bookmarks at the exact same spot.
        # This creates a 0ms chapter which is useless. 
        # We skip this entry so the NEXT bookmark takes over from the same start time.
        if ($end -eq $start) { 
            continue 
        }

        # Safety: Ensure End > Start (e.g. if bookmarks were out of order)
        if ($end -le $start) { $end = $start + 1000 }

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

    foreach ($vid in $videoFiles) {
        $tsName = "$($vid.BaseName).ts"
        $tsPath = Join-Path $tempFolder $tsName
        
        $escapedPath = "TempTS\$tsName".Replace("'", "'\''")
        "file '$escapedPath'" | Out-File -FilePath $fileList -Append -Encoding ascii

       # Select Encoding Parameters
        if ($useNvenc) {
            # Full Hardware Acceleration (Decode + Encode)
            # We use a Hybrid approach (GPU Decode -> CPU Filters -> GPU Encode) 
            # This ensures the 'pad' filter works correctly without complex VRAM management
            $inputParams    = @("-hwaccel", "cuda")
            $encodingParams = @("-c:v", "h264_nvenc", "-rc", "vbr", "-cq", "23", "-preset", "p4")
            $videoFilter    = "scale=${targetRes}:force_original_aspect_ratio=decrease,pad=${targetRes}:(ow-iw)/2:(oh-ih)/2"
        } else {
            # CPU Encoding
            $inputParams    = @()
            $encodingParams = @("-c:v", "libx264", "-crf", "23", "-preset", "fast")
            $videoFilter    = "scale=${targetRes}:force_original_aspect_ratio=decrease,pad=${targetRes}:(ow-iw)/2:(oh-ih)/2"
        }

        # Build Full Arguments
        $args = @("-hide_banner", "-loglevel", "error") + $inputParams + @(
            "-i", $vid.FullName
        ) + $encodingParams + @(
            "-vf", $videoFilter,
            "-r", $targetFps,
            "-c:a", "aac", "-b:a", "192k",
            "-ac", "2", "-ar", "48000",
            "-af", "aresample=async=1",
            "-bsf:v", "h264_mp4toannexb",
            "-f", "mpegts",
            "-muxdelay", "0",
            "-y",
            $tsPath
        )

        # Queue Job
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

        Write-Host "Queueing: $($vid.Name)"
        $runningJobs += Start-Process -FilePath "ffmpeg" -ArgumentList $args -NoNewWindow -PassThru
    }

    # Wait for completion
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

    # 3. Concatenate and Inject Chapters
    if (Test-Path -LiteralPath $fileList) {
        Write-Host "Concatenating files and embedding chapters..."
        
        $concatArgs = @(
            "-hide_banner",
            "-f", "concat",
            "-safe", "0",
            "-i", $fileList
        )

        # Inject Metadata if exists
        if (Test-Path -LiteralPath $metaFile) {
            $concatArgs += @("-i", $metaFile, "-map_metadata", "1")
        }

        $concatArgs += @(
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
        if (Test-Path -LiteralPath $metaFile) { Remove-Item -LiteralPath $metaFile -Force }
    } else {
        Write-Host "No files were successfully processed." -ForegroundColor Red
    }
}

Write-Host "`nAll operations complete." -ForegroundColor Cyan
Read-Host "Press Enter to close..."