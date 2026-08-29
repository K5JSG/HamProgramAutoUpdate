# Ham Radio Updater Dashboard

A centralized dashboard application designed to manage, monitor, and execute various ham radio and logging software updates. It automates scheduling, tracks execution history, and provides an easy-to-use interface to run or clear update logs individually or globally.

## Features
 - Centralized Dashboard: View the status of all supported update programs in one place.
 - Execution Tracking: Displays the timestamp of the last run, whether it was successful or not, and the last actual update date for each program. If an update has happened in the last 7 days that program is highlighted green.
 - Persistent Update Dates: The "last updated" timestamp is retained even if the execution logs are cleared.
 - Limited Log History: Logs display the last three run instances for each program.
 - Flexible Execution: Run individual updaters or trigger all of them simultaneously. Log histories can also be cleared individually or all at once.
 - Windows Task Scheduler Integration: Automatically creates tasks to run the program nightly at 03:00 AM and upon user logon.

## Supported Programs
 - BktTimeSync
 - CHIRP Next
 - Gridtracker
 - Ham Radio Deluxe
 - N1MM+ Logger+
 - Netlogger
 - [POTA Activator Park Activations](https://github.com/K5JSG/POTA-Activator-Park-Activations)
 - RT Systems
 - TQSL
 - WSJT-X
 - More Soon???