# RS3CoreLasso
This project aims to prevent issues with the game not launching when running multiple accounts (usually due to the openGL driver running out of memory).
It does this by hooking into the ETW [https://learn.microsoft.com/en-us/windows-hardware/test/wpt/event-tracing-for-windows](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/event-tracing-for-windows) which essentially hooks into the Windows kernel and asks it to notify the application about numerous events (in the case of this application, it detects when an application is starting).

Once the client starts it will force the client onto specific cores, and limit the number of cores it is allowed (e.g. 2 cores per client) as it is suspected that the game attempts to create an openGL context for every available thread, which causes a memory exausation issue.

## Config
in the appsettings.json file, there are three options:
**CoresPerProcess**: the number of cores to assign to each client, which defaults to 2, depending on how many clients you are running, you may need to reduce this to 1.
**GameCoreList**: will set which cores available to use for the client**\***, defaults to ALL cores if not specified.
**ShaderCoreList**: will set which cores available to use for the client**\*\***, defaults to ALL cores if not specified.

**\*** cores available to the main game client (what you see)
**\*\*** cores available to the client for compiling shaders (may not require that many cores, not tested)

## Usage
Run the executable as admin, launch clients, done.
You can either leave the application running after launching all clients, so that if a client crashes, you can instantly relaunch, otherwise, closing and relaunching the app should also work.
**note**: by default, the config assumes you have a 24 core system and will assign cores 4-7 to shader compilation clients, and 8-24 for the game client.
