# Accept the name passed from the Batch file
param([string]$OutputName = "MergedScript")

$inputFolder = ".\Input"
# Ensure the filename ends in .funscript
$outputFile = "$OutputName.funscript"


# 1. Check if Input folder exists
if (-not (Test-Path $inputFolder)) {
    Write-Host "Folder 'Input' not found. Creating it..." -ForegroundColor Yellow
    New-Item -ItemType Directory -Path $inputFolder | Out-Null
    Write-Host "Please put your .funscript files in the 'Input' folder and run this again."
    exit
}

# 2. Get files sorted alphabetically
$files = Get-ChildItem -Path $inputFolder -Filter *.funscript | Sort-Object Name

if ($files.Count -eq 0) {
    Write-Host "No .funscript files found in '$inputFolder'." -ForegroundColor Red
    exit
}

Write-Host "Found $($files.Count) files. Merging Multi-Axis (Standard)..." -ForegroundColor Cyan

# --- GLOBAL STORAGE ---
# 1. Main script actions (The "actions" at the root of the file)
$globalRootActions = New-Object System.Collections.Generic.List[PSCustomObject]

# 2. Auxiliary axes (The items inside the "axes" array, grouped by ID)
# Key = Axis ID (e.g. "R1", "Twist"), Value = List of Actions
$globalAuxAxes = @{}

$currentOffset = 0

# 3. Process files
foreach ($file in $files) {
    Write-Host "Processing: $($file.Name)"
    
    try {
        # Read file safely
        $jsonContent = Get-Content -LiteralPath $file.FullName | Out-String
        $json = $jsonContent | ConvertFrom-Json
        
        # Track the longest duration in this specific file to update the global offset later
        $maxTimeInFile = 0
        $hasActions = $false

        # --- PROCESS ROOT ACTIONS (Main Axis) ---
        if ($json.actions) {
            $hasActions = $true
            foreach ($action in $json.actions) {
                $newTime = [int]$action.at + [int]$currentOffset
                
                $newAction = [PSCustomObject]@{
                    at = $newTime
                    pos = $action.pos
                }
                $globalRootActions.Add($newAction)

                if ($action.at -gt $maxTimeInFile) { $maxTimeInFile = $action.at }
            }
        }

        # --- PROCESS AUXILIARY AXES (R1, L1, Surge, etc.) ---
        if ($json.axes) {
            $hasActions = $true
            foreach ($axisObj in $json.axes) {
                $axisId = $axisObj.id
                
                # Create storage for this Axis ID if it's new to us
                if (-not $globalAuxAxes.ContainsKey($axisId)) {
                    $globalAuxAxes[$axisId] = New-Object System.Collections.Generic.List[PSCustomObject]
                }

                # Add actions for this specific axis
                foreach ($action in $axisObj.actions) {
                    $newTime = [int]$action.at + [int]$currentOffset

                    $newAction = [PSCustomObject]@{
                        at = $newTime
                        pos = $action.pos
                    }
                    $globalAuxAxes[$axisId].Add($newAction)

                    if ($action.at -gt $maxTimeInFile) { $maxTimeInFile = $action.at }
                }
            }
        }

        # --- UPDATE OFFSET ---
        if ($hasActions) {
            # If the file had metadata duration, check if it's longer than the last action
            # (Sometimes scripts end before the video ends)
            if ($json.metadata -and $json.metadata.duration) {
                $metaDur = [int]($json.metadata.duration * 1000) # Convert seconds to ms
                if ($metaDur -gt $maxTimeInFile) {
                    $maxTimeInFile = $metaDur
                }
            }
            
            # Move the offset forward by the duration of this file
            $currentOffset += $maxTimeInFile
        } else {
            Write-Host "  Warning: No actions or axes found in $($file.Name)" -ForegroundColor Yellow
        }
    }
    catch {
        Write-Host "Error reading $($file.Name): $($_.Exception.Message)" -ForegroundColor Red
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

# Rebuild the "axes" array from our dictionary
foreach ($key in $globalAuxAxes.Keys) {
    $axisObj = [Ordered]@{
        id = $key
        actions = $globalAuxAxes[$key]
    }
    $finalObj.axes += $axisObj
}

Write-Host "Saving to $outputFile..."
$finalObj | ConvertTo-Json -Depth 10 | Set-Content $outputFile -Encoding UTF8
Write-Host "Success! Saved to $outputFile" -ForegroundColor Green