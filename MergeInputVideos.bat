@echo off
setlocal enabledelayedexpansion

:: 1. Setup folders
if exist filelist.txt del filelist.txt
if not exist "TempTS" mkdir "TempTS"

echo.
echo ========================================================
echo  Step 1: Normalizing inputs to Intermediate Streams
echo ========================================================
echo.

:: 2. Loop through files and convert to Temporary TS files
:: We re-encode here to standard H.264/AAC to ensure all parts match perfectly.
:: -muxdelay 0 : Prevents the encoder from adding a startup delay
for %%i in (Input\*.mp4) do (
    echo Processing: %%~nxi
    
    ffmpeg -hide_banner -loglevel error -i "%%i" ^
    -c:v libx264 -crf 23 -preset fast ^
    -c:a aac -b:a 192k ^
    -bsf:v h264_mp4toannexb ^
    -f mpegts ^
    -muxdelay 0 ^
    "TempTS\%%~ni.ts"

    :: Add the new .ts file to the list
    echo file 'TempTS\%%~ni.ts' >> filelist.txt
)

:: 3. Safety Check
if not exist filelist.txt (
    echo ERROR: No files processed.
    pause
    exit /b
)

echo.
echo ========================================================
echo  Step 2: Concatenating Files
echo ========================================================
echo.

:: 4. Concatenate the TS files and save as MP4
:: We use -c copy here because we already did the heavy lifting in Step 2.
ffmpeg -hide_banner -f concat -safe 0 -i filelist.txt -c copy -bsf:a aac_adtstoasc -movflags +faststart Combined.mp4

:: 5. Cleanup (Optional - remove "::" to enable deletion)
::rd /s /q "TempTS"
::del filelist.txt

echo.
echo Process completed! Result is in Combined.mp4
pause