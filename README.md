EroScripts disscussion: https://discuss.eroscripts.com/t/attcks-merge-tool-combines-videos-and-funcscripts/290278

I wanted to share my merge scripts, these will eventually be converted into a proper application with a GUI. However for now this might be useful to someone.

## How To Install:
Note: This is a windows batch file and PowerShell Script. I'd expect it Only work on modern windows systems.
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
1. Add Videos and Funscripts to Input Folder. The script will merge them in Alphabetical order. To control the order simply rename the source files, 1,2,3,4 etc. Be sure that the video and funscripts are the same name or at least the same order alphabetically.
2. Run batch file "DoTheMerges"
3. It will prompt for a resulting file name. Enter one or paste one, avoid special characters. Hit enter to submit it and continue.
4. The Script will first merge the funscripts. Spitting them out next to the batch scripts. This process assumes that there is only 1 funscript file per video. If you have multiple per video see notes below.
5. It will pause after completing the first step incase you only want to do the funscript merge. If so simply close the window if you don't want to merge the videos. Hitting any key will continue the script to video merging.
8. The Video merge will first convert the source videos into steams using the same settings so they all match. This will take a while, longer video = longer encoding phase. It will tell you what video it's on as it moves through the list.
9. Once all the videos are converted to streams it will concat them (joining them together in order). The final video will be created next to the batch scripts.

## Notes: 
- If you're combining different funscripts or have multiple files per video make sure you have combined them into 1 funscript file before running the merge tool batch files.
- There is no configuration options for the video encoding without changing the batch file at this time.
- If you get errors about ffmpeg not being found/recognized restart the machine. It's likely not in the PATH, google how to fix that.


