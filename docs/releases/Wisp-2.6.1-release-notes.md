# Wisp 2.6.1

[Download Wisp 2.6.1 for Windows](https://github.com/Views2k/Wisp/releases/download/v2.6.1/Wisp-Setup-2.6.1.exe)

## Clips

- Fixed clipping becoming unavailable after closing Forza. Wisp now recovers automatically without needing a restart.
- Fixed audio timing slips and recoverable audio glitches clearing the clip buffer. Recording keeps its footage when audio is temporarily unavailable.
- Improved HDR recording and playback colors. Added optional 10-bit HDR recording on supported NVIDIA hardware; standard recording uses SDR H.264 by default.
- Open and prepare clips while Forza stays running. HDR lossless clips show preparation progress and reuse available playback copies on later opens.
- Added a loading spinner and disabled Play and seeking while the player is preparing or buffering. Clips stay paused until you press Play.
- Added compatible SDR H.264 exports for HDR and lossless clips, while keeping the original recording. Fixed failed exports of large lossless clips and added specific export errors with **Copy details**.
- Files deleted outside Wisp now disappear from the gallery when you refresh or return to Clips.
- Added dismiss and **Don't show again** controls to clip reminders. Turning off new-clip reminders keeps recording errors visible, and dismissed unfinished-save notices stay dismissed after restarting Wisp.

## Tune

- Fixed Tune reads failing because players have different game database contents.
- Fixed reads being rejected when stored setup data differs from the live car's tune.
- Added clearer guidance when the current car is unavailable, separate messages for failed reads and unsupported game updates, and **Copy details** for reporting problems.

## Lap review and diagnostics

- Fixed a crash when pressing cursor arrow keys with no lap points available.
- Added local crash reports and recent performance history to help investigate crashes, stalls and HUD problems.
- Added **Copy crash details** on the next launch after a reported error, including matching Windows fault information after an unexpected exit.
- **Export debug ZIP** now includes saved error reports and recent performance information even when detailed logging is off. **Delete local logs** also clears saved crash reports.
