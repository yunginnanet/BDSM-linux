# BDSM-linux

The Best Downloader for Sideloader Mods is an update client for Honey Select 2 and AI Shoujo mods using the BetterRepack
mod repository. It is meant as a faster alternative to the existing KKManager standalone updater, and can be run
unattended with Windows Task Scheduler.

### Fork

This fork allows you to run this application in operating systems that *aren't complete fucking garbage*. It also
removes some *completely moronic obfuscation that serves no purpose*. Anyone that is going to read the source code of a
program that downloads mods for a hentai game is probably not going to be deterred by a few lines of code. It's a cool
program man.. but wtf!?

This is intended for those of us chads that run illusion games in WINE.

### Usage

- [install .NET SDK 7.0 for linux](https://learn.microsoft.com/dotnet/core/install/linux?WT.mc_id=dotnet-35129-website)
- `git clone https://github.com/yunginnanet/BDSM-linux.git`
- `cd BDSM-linux`
- `dotnet run`

### Status

As of writing the thing runs, going to test it fully momentarily.

#### Known issues

- progress bars had to be disabled because the library does not support them on linux. 
