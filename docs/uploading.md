# Uploading files to the machine

The **Upload** button (the `uploadFile` command) sends the open G-code file to the machine. Without an open file it asks
for one. While an upload runs the button reads "Cancel: Uploading 42%" and pressing it again cancels. The command is only
available while the machine is idle.

Settings, under *File upload*:

- **Folder on the machine:** default `/sd/gcodes`.
- **Send as .lz files:** *Auto* (the default) sends `.lz` when the machine's `version` reply says `ftype = lz`, as the Python
  controller does. *On* and *Off* override that.

## How it works

`Carvera.Core/Transfer` ports the legacy (Smoothie protocol) upload of the Python controller:

1. The MD5 of the original file is computed. With `.lz` the file is first converted (see below).
2. Polling stops and the controller hands its byte stream to the transfer (`RunExclusiveAsync`). Any other command is
   refused until the transfer ends; the status poll resumes afterwards.
3. `upload <path>` is sent, then the file goes out as XMODEM with 8192-byte packets and CRC. Packet 0 carries the MD5 as text
   and every payload is preceded by its length. The receiver's cancel byte is 0x16.
4. For `.lz` uploads the machine then unpacks the file and reports `decompart = N` per block; the state shows
   "Decompressing" until all blocks are done.

Progress is published as `transfer.active`, `transfer.phase`, `transfer.percent`, `transfer.name` and `transfer.message`, so
layouts can show it. The machine's accepted file types are in `machine.fileType`.

## Known limits

- **The `.lz` blocks are stored, not compressed.** They are valid QuickLZ level-1 blocks, so the machine should unpack them,
  but the upload is as long as the original file. This format has not been checked against a real machine: if uploads fail
  with *Auto*, set *Send as .lz files* to *Off*.
- Only the plain-text protocol is supported. Machines that use the framed Makera protocol are not handled.
- No remote file browser yet, so the folder is a setting rather than a picker.
