

=========

1. Remote File Sync Software

Role: Act as an application development team specializing in .NET 10, C#.

Context: Need to develop a remote file synchronization software for Windows operating system.

The Task: Create a comprehensive implementation plan for the remote file synchronization software. 

Implementation details:
  1. Software should be able to synchronize files in a specified folder on one computer to a specified folder on another computer.
  2. Software should be a single executable file with no configuration.
  3. All necessary confgurations will be provided by command line options.
  4. Software has two modes as follows:
  - Server Mode: Waits for the client connection
  - Client Mode: Connects to the server
  5. Optionally bi-directional synchronization is supported.
  6. On a bi-directional mode, Server and Client scan their own folders and compare the files in folder by folder.
  7. To decide the newest version on both sides, use modification time(GMT) and file size.
  8. Outdated files will be moved to [Specied Folder]/yyyyMMdd/[Relative Folder to Sync Folder]/[File Name] on each server.
  9. To transfer the files, it should be compressed. But, if the files are already compressed, transfer them as is.
  10. The block size can be set by command line options(Default is 64 KB) 
  11. Software can be multi-threaded for higher performance (add a option for MaxThread,
default is 1)
  10. Software should be able to run on Windows 10 and Windows 11.

Requirements & Constraints:
  1. Create a new git branch. And, commit and push for every modification, every phase to a new branch.
  2. Use brainstorming skill and create a team with expert teammates
  3. When using multiple agents, use psmux(tmux compatible) multiple split panes
  4. Review the document and fix the errors.
  5. Detailed Code Snippets - Provide the exact C# code changes required for the target files.
  6. Output Format - Format the final output as a professional Markdown (.md) document, structured so it is ready to be saved directly to the E:\RemoteFileSync\Plans\ folder.

----
Test Bi-Directional Sync between "H:\SyncTest\NET10" and "H:\BK_20260320\NET10", both are in localhost, but assume they are remote.


==========

2. Resolve file deletion issue.
On file deletion, the deleted files are exists in one server but not in another server. 
Case (1) If the files are deleted on Server-A and the files are untouched(modification time is earlier than previous sync date/time) on Server-B
        - RemoteFileSync must delete the files on Server-B.
Case (2) If the files are deleted on Server-A and the files are modified(modification time is later than previous sync date/time)on Server-B
        - RemoteFileSync must copy the files from Server-B to Server-A.
How to resolve this issue? Any idea?

Review documents below files.
  - e:\RemoteFileSync\Plans\2026-03-26-remote-file-sync-design.md
  - e:\RemoteFileSync\Plans\2026-03-26-remote-file-sync-implementation-plan.md 
  - e:\RemoteFileSync\Plans\2026-03-26-remote-file-sync-usage-guide.md

Review the exsiting codes in the codebase.

And, provide any solution for this issue and create a comprehensive implementation plan.

Requirements & Constraints:
  1. Create a new git branch. And, commit and push for every modification, every phase to a new branch.
  2. Use brainstorming skill and create a team with expert teammates
  3. When using multiple agents, use psmux(tmux compatible) multiple split panes
  4. Review the document and fix the errors.
  5. Detailed Code Snippets - Provide the exact C# code changes required for the target files.
  6. Output Format - Format the final output as a professional Markdown (.md) document, structured so it is ready to be saved directly to the E:\RemoteFileSync\Plans\ folder.


==========

3. WPF Blazor hybrid UI application implementation plan

Role: Act as an application development team specializing in .NET 10, C# and WPF Blazor.

The Context: Since the command-line options are too complicated, need to create UI application to select all options for RemoteFileSync app.

The Task: Create a comprehensive implementation plan for the WPF Blazor UI interface to select all options for RemoteFileSync app.

Implementation details and files;
  1. Review User's Guide to check all the command-line options
    - User's Guid: E:\RemoteFileSync\Plans\2026-03-26-remote-file-sync-usage-guide.md
  2. Review existing codes in the codebase.
    - Source Code Folder: E:\RemoteFileSync\src\RemoteFileSync\
  3. Create a WPF Blazor UI app project in E:\RemoteFileSync\src\ folder and the project name will be "ExecRFS" (or any recommendation?)
  4. Implement UI components for all command-line options.
  5. Communicate with RemoteFileSync.exe app to check the progress, errors or any messages and currently transferring files for each thread.
  6. Start/Stop/Pause and Option Generate buttons must be implemented.
  7. Live log viewer must be implemented.
  8. Save and load option selections and pre-load last time used options.

Requirements & Constraints:
  1. Create a new git branch. And, commit and push for every modification, every phase to a new branch.
  2. Use brainstorming skill and create a team with expert teammates
  3. When using multiple agents, use psmux(tmux compatible) multiple split panes
  4. Review the document and fix the errors.
  5. Detailed Code Snippets - Provide the exact C# code changes required for the target files.
  6. Output Format - Format the final output as a professional Markdown (.md) document, structured so it is ready to be saved directly to the E:\RemoteFileSync\Plans\ folder.


-----
- File Deletion and Non-Existing files have issues. For the file version control, how about using SQLite with indices. Review the codes for file version check/deleted/non-existing file handling. And, brainstorm to find the solution.

-  Do not store content hashes, I don't want CPU/IO overhead for calculating the content hashes.

- Conduct code reviews for each step