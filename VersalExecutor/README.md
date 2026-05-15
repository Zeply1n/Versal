# Versal Executor

## Build
1. Open `VersalExecutor.csproj` in Visual Studio 2022
2. Set platform to **x64**
3. Press F5

No QuorumAPI reference needed in the project — both APIs are loaded at runtime from:
- `Bin\quorum\QuorumAPI.dll`  (Quorum, default)
- `Bin\lx63\QuorumAPI.dll`    (LX63, select via titlebar chip)

## Switch API
Click the **Quorum** chip in the titlebar → pick LX63 → Apply.
