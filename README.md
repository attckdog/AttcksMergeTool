I wanted to share my merge scripts, these will eventually be converted into a proper application with a GUI. However for now this might be useful to someone. Let me know if you have any problems.

## How To Install:
Note: This is a PowerShell Script. I'd expect it Only work on modern windows systems.
1. Install ffmpeg, Open command prompt and run this command
 ```
winget install Gyan.FFmpeg
```
3. Get the scripts from my  https://github.com/attckdog/AttcksMergeTool
4. Click code in the top right (green button)
5. Select download Zip
6. Extract the zip somewhere in a new folder (I'd recommend 7zip)
7. Create a Folder next to the scripts named "Input"

## How to Use
1. Add Videos and Funscripts to the Input Folder. The script will merge them in Alphabetical order. To control the order simply rename the source files, 1,2,3,4 etc. Be sure that the video and funscripts are the same name.
1. (Optional) If you have an nvida GPU open the MergeScriptsAndVideos.ps1 file with a text editor and set NVEC to $true, save and close. Most people should do this. It will error and stop worst case, So try it.
2. Right click MergeScriptsAndVideos.ps1 and click run with powershell.
3. It will prompt for a resulting file name. Enter one or paste one, avoid special characters. Hit enter to submit it and continue.
4. The Script will first merge the funscripts. Spitting the merged result out next to the batch scripts. If you have multiple per video see notes below.
   - Multi-axis is supported in either all in 1 funscript files OR separate axis per file. 
8. The Video merge will first convert the source videos into steams using the same settings so they all match. This will take a while, longer video = longer encoding phase. It will tell you what video it's on as it moves through the list. Enable NVEC if you can.
9. Once all the videos are converted to streams it will concat them (joining them together in order). The final video will be created next to the batch scripts.

## Notes: 
- If you're combining different funscripts or have multiple files per video make sure you have combined them into 1 funscript file before running the merge tool batch files. Multi-Axis files should use the convention of {SameFileNameAsVideo}{Axis}.funscript. example: TifaVideo.roll.funscript OR be embedded as axis in the main funscript.
- If you get errors about ffmpeg not being found/recognized restart the machine after installing it. It's likely not in the PATH, google how to fix that it's simple.

I'll update this post when I have the real app working.


## Changes:
- 2025-12-04
  - Updated to handle separate files for the different axis. IE; .roll.funscript 
  - Fixed a problem with desync caused by video and script length not being the same.
- 2025-12-06
  - Combined everything into 1 powershell script.
  - Added support NVEC encoding 
  - Added chapter/bookmark support
  - Added retaining of source funscript metadata where possible
  - Added video chapter support but it's kind of buggy
  - Added loading bar for the encoding.
