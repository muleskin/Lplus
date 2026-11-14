# Lplus

A small Windows desktop chat app for AI models that run on your own PC.

Lplus starts the local model server for you, keeps it running in the tray, and gives you a clean
chat window on top of it. Your chats stay on your machine; the only thing that leaves it is a web
lookup, when you let a model make one.

![Lplus chat window](app/shots/demo.png)

## Download

Get `Lplus.exe` from the [latest release](https://github.com/muleskin/Lplus/releases/latest).
It is one self-contained file for 64-bit Windows, so you do not need to install .NET. Download it and run it.

The [user guide](https://github.com/muleskin/Lplus/releases/latest/download/Lplus-User-Guide.docx)
(Word) is on the same page.

## What it does

- **Chat with local models.** Replies stream in as they are written, with Markdown, code blocks
  with a Copy button, and a collapsible "thinking" section for models that reason first.
- **Model picker.** Lists the models you have installed and downloads a new one when you pick it.
- **Images.** Attach pictures for models that can see.
- **The clock.** Models that can call tools read your PC's clock, so "what time is it?" gets the
  real answer. Other models are simply told the time.
- **Chat and Code modes.** A switch in the message box. In Code mode the **+** button picks a
  project folder, and the model can list, read and search its files and, after you confirm each
  file, create or replace them, so "update my app" starts from the code you already have.
  Nothing outside that folder can be reached.
- **Going online.** Models that can call tools can search the web and read pages when they need
  to look something up, and say where they found it. There is a switch for it in Settings.
- **Working on an existing project.** In Code mode the model reads your code, changes it, and
  runs build, test and run commands in the project folder (`dotnet build`, `npm test`, ...) to
  find and fix problems. Lplus works out how the project is built and tested (.NET, Node.js,
  Python, Rust, Go, Java, CMake, Make) and, each time the model says it is finished, builds and
  tests the project itself and sends any errors back, so the model keeps going until it passes.
- **Scripts.** For checks that take more than one command (the state of a project, what
  changed, counts and sizes, settings), the model writes a PowerShell, Python, cmd or bash script
  and runs it in the project folder, or saves one in the project and runs it by its path. You
  are shown the whole script before it runs.
- **You stay in charge.** Every file change and every command asks first, unless you turn on
  **Work without asking** in Settings: then file changes and ordinary build and test commands go
  ahead on their own, and anything else still asks. A copy of each changed file is kept, and
  **Undo file changes** under the reply puts them all back.
- **MCP servers.** The MCP servers page (in the sidebar) lists your Model Context Protocol
  servers with how each is doing. Add, edit, remove, switch on or off and connect them there,
  filter the list, or search the public MCP registry and add a server from the results. **Import** brings in the servers already set up for Claude Code or the Claude desktop app. Servers
  can be programs Lplus starts (including ones reached through `ssh`) or web addresses; their tools are offered to the model in both
  modes, and each tool asks before its first use. When your servers have more tools between
  them than a model handles well, a large server's tools are offered only when you name the
  server in your message.
- **Copy anything.** Drag to select any text in the conversation and press Ctrl+C, or right-click
  to copy a message, the thinking, or the whole chat.
- **Prompt history.** Up and Down in an empty message box walk through your earlier prompts.
- **Chat history.** Chats are saved in a local SQLite database, grouped by Today / This week /
  Older, and can be renamed or deleted.
- **GPU status bar.** Shows your graphics card, which model is loaded, and how much of it sits in
  GPU memory.
- **Any server.** Use the model server on this PC, or one on another computer such as a rented
  cloud GPU pod, by its address.
- **Settings.** Where the model server is, cloud models on or off, expose the server to your network, where models are
  stored, tools, web access, working without asking and the project folder, and context length (4k to 256k).
- **Runs in the tray.** Closing the window keeps the server running; quit from the tray icon to
  stop it.

## What you need

- 64-bit Windows.
- A local model server installed. Lplus looks for it next to its own exe, in the usual install
  folder, and on your `PATH`. If a server is already answering on port 11434, Lplus uses that one.

## Building

Needs the .NET 10 SDK on Windows.

```powershell
cd app
dotnet run                                          # run from source
dotnet publish -p:PublishProfile=SingleExe          # single self-contained exe in app\publish\Lplus.exe
```

C# WinForms, drawn with its own controls. The only package is `Microsoft.Data.Sqlite`.

## Command line

| Option | What it does |
| --- | --- |
| `--selftest <report.txt> [model]` | Starts the server, lists models, asks one short question, checks the chat store, and writes a report. |
| `--toolrun <report.txt> <model> <folder> <question>` | Asks one question with the tools on and writes down every tool step and the answer. No file is written and no command is run, unless `apply` follows the question. |
| `--mcp-check <report.txt>` | Connects to every server in `mcp.json` and writes down how each answered: its protocol, what it offers and its tools. No tool is run. |
| `--screenshot <file.png>` | Draws the window to a PNG and exits, using a throwaway copy of the database. |
| `--quit` | Asks a running copy to quit and stop the server it started. |

## A model server on another computer

Larger models need more graphics memory than most PCs have. Lplus can use a model server
running somewhere else, such as a rented cloud GPU pod, instead of the one on this PC:

1. On the other computer, start the model server so that it listens on all addresses, and make
   its port (11434) reachable. A cloud provider's tutorial for running the server on a pod covers
   both; a pod then has a web address such as `https://<pod id>-11434.proxy.runpod.net`.
2. In Lplus open **Settings**, and next to **Server location** click **Change**.
3. Choose **Another computer or a cloud pod**, enter the address, and click **Test**. A key is
   only needed for a server that asks for one.
4. Click **Use this server**. The models on that server appear in the model picker, and picking a
   name it does not have downloads the model onto it.

Lplus does not start or stop a server on another computer, and the settings for exposing the
server, the model location and cloud models apply only to the server on this PC. A server reached
by a public address has no password of its own: anyone who learns the address can use it, so stop
the pod when you are done. Your prompts, and in Code mode the files the model reads, are sent to
that computer.

## Build and test commands

Lplus picks the commands from what it finds in the project folder. To set them yourself, put a
`.lplus.json` in the folder; an empty string means "none":

```json
{ "build": "dotnet build app/App.csproj", "test": "dotnet test tests/Tests.csproj" }
```

## MCP servers

The servers are kept in `%LocalAppData%\Lplus\mcp.json`, which uses the usual layout. A server is either a program to start, or
a web address:

```json
{
  "mcpServers": {
    "files": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "D:\\Notes"] },
    "remote": { "url": "https://example.com/mcp", "headers": { "Authorization": "Bearer ..." } }
  }
}
```

`env` sets environment variables for a program, and `"disabled": true` skips a server. The MCP
servers page writes this file for you; if you edit it by hand, click **Reload** on that page.
Each server is listed there with its number of tools, or with the reason it did not start.

## Where it keeps things

`%LocalAppData%\Lplus`: `db.sqlite` (chats and settings), `mcp.json`, `app.log`, `server.log` and
attached images. Set `LPLUS_DATA_DIR` to use another folder.
