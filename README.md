# ClangFormatTask  
Incremental [clang-format](https://clang.llvm.org/docs/ClangFormat.html) integration for MSBuild and Visual Studio C++ projects.  

## Overview
ClangFormatTask is a custom MSBuild task that adds incremental clang-format support to Visual Studio 2022 and MSBuild 17.5+.  
It uses the [clang-format](https://clang.llvm.org/docs/ClangFormat.html) tool to format C and C++ source files.  
It runs [clang-format](https://clang.llvm.org/docs/ClangFormat.html) automatically before compilation but only processes files that have changed. Rebuild forces formatting of all files. The task also supports running clang-format in parallel across multiple processes.

## Key features
- Incremental formatting based on MSBuild Inputs/Outputs  
- Automatic integration before the C++ compile step  
- Rebuild forces formatting of all files  
- Optional parallel formatting across multiple clang-format processes  
- Simple integration through an MSBuild .targets file

## Requirements
- Visual Studio 2022 (MSBuild 17.5 or later)  
- clang-format installed  
- .NET Framework 4.7.2 or later for building the task assembly

## Installation
1. Build the ClangFormatTask project (produces ClangFormatTask.dll).  
2. Place ClangFormat.targets next to the DLL.  
3. Import the targets file, for example via Directory.Build.targets in your solution root:

```xml
<Project>
  <Import Project="Build/ClangFormat.targets" />
</Project>
