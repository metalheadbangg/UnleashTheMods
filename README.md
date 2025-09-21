# Dying Light: The Beast - Mod Merge Utility

A powerful command-line tool designed to intelligently merge script mods for the game **Dying Light: The Beast**, preventing conflicts and ensuring a stable gameplay experience.
This tool was created to solve script .scr file conflicts.
UTM uses a key-based analysis. Identifies specific functions and parameters within the script files. Then rebuilds final script file, line by line, using the original game file as a template.

---

### Key Features

* Understands the content of script files to merge changes to different functions within the same file from multiple mods.
* When two or more mods attempt to change the exact same function in different ways, the tool prompts the user to choose which version to keep.
* For files with many conflicts, you can choose to prefer one mod's changes for all subsequent conflicts within that file, speeding up the process.
* Automatically reads .pak files from within .zip, .rar, and .7z archives, so you can drop mods downloaded directly from Nexus into your `mods` folder.
* After merging all scripts, the tool generates a single, dataX.pak file (e.g. data3.pak) and places it directly into your game's `source` directory.

---

## Requirements

The only requirement to run this tool is the **.NET 8.0 Desktop Runtime**. If you don't have it, the application will not start.

> [!IMPORTANT]
> [Download .NET 8 Desktop Runtime](https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/8.0.20/windowsdesktop-runtime-8.0.20-win-x64.exe)


---

## Installation & Usage

1.  Navigate to the **Releases** page of this project's GitHub repository.
2.  Download the latest release.
3.  Extract the contents of the downloaded zip file.
4.  Place .exe file into your game's **ph_ft** folder. The final path should look like this:
    ```
    ...\Dying Light The Beast\ph_ft\
    ```
5.  In that same **ph_ft** folder, create a new folder named `mods`.
6.  Place all your downloaded mods (**.zip**, **.rar**, **.7z**, or **.pak** files) directly into the `mods` folder.
7.  Double-click the *.exe* file to run the merger.
8.  Follow the on-screen prompts to resolve any conflicts.
9.  Once the process is complete, a new, merged **dataX.pak** file will be created in the following directory, ready for the game to use:
    ```
    ...\ph_ft\source\
    ```

---

## Libraries and License

This project was made possible with the help of the following open-source library:

 * **[SharpCompress](https://github.com/adamhathcock/sharpcompress):** A versatile library used to handle **.rar** and **.7z** archive extraction.

This project is licensed under the MIT License.

```
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

</details>
