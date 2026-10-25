# Running Pulaski Edge

Pulaski Edge is one worker program (D-050). It runs the same way as a console program, a container, a Windows service or a
systemd unit; only how it is started and stopped differs (#25).

## Publish

```sh
# Windows
dotnet publish src/Pulaski.Edge -c Release -r win-x64 --self-contained -o publish/edge-win
# Linux
dotnet publish src/Pulaski.Edge -c Release -r linux-x64 --self-contained -o publish/edge-linux
```

Copy the output to the edge machine and fill in `appsettings.json` beside the program: the configuration from the edge node's
**Setup** tab in Pulaski, the edge user's password and any connection secrets. `Edge__*` environment variables override it.

## Windows service

From an elevated PowerShell on the edge machine:

```powershell
.\deploy\edge\windows\install-service.ps1 -Path "C:\Program Files\Pulaski Edge\Pulaski.Edge.exe"
Start-Service PulaskiEdge
```

- **Account:** `NT AUTHORITY\LocalService`. The buffer lives in `C:\ProgramData\Pulaski Edge\pulaski-edge.db` and the OPC UA
  certificates in `C:\ProgramData\Pulaski Edge\pki`; the script creates the folder and lets that account write it.
- **Start:** automatic (delayed), so the network is up first.
- **Stop:** Pulaski Edge closes its connections and finishes the batch it is forwarding, within 15 seconds. Whatever wasn't
  forwarded stays in the buffer and goes on the next start.
- **Recovery:** after a crash or a non-zero exit, restart after 10 s, then 30 s, then every 60 s; the count resets after a day.
  Pulaski Edge exits with 1 when one of its workers fails.
- **Logs:** Windows Event Log, *Application*, source `PulaskiEdge`: warnings and errors, plus Pulaski Edge's own information
  messages (connections started, forwarding resumed). Change the levels under `Logging:EventLog` in `appsettings.json`.
- **Remove:** `.\deploy\edge\windows\uninstall-service.ps1`. The buffer is kept.

## systemd

See the comments at the top of [`linux/pulaski-edge.service`](linux/pulaski-edge.service). The unit uses `Type=notify` (Pulaski
Edge tells systemd when it has started and is stopping), keeps the buffer in `/var/lib/pulaski-edge`, restarts on failure after
10 s, and logs to the journal (`journalctl -u pulaski-edge`).

## OPC UA certificates

An OPC UA connection signs and encrypts by default (#22). The edge makes its own application certificate the first time, in
`pki/own` (`Edge:PkiPath`, beside the program unless set). It trusts a server's certificate only when:

- it is in `pki/trusted/certs`, or
- its SHA-1 thumbprint is the connection's **Server thumbprint** (the edge then copies it into `pki/trusted`), or
- the connection has **Trust any server** ticked (for a simulator only).

A server it refuses leaves its certificate in `pki/rejected/certs`: check it, then move it to `pki/trusted/certs`. The server
must trust the edge's certificate too (`pki/own/certs`), the same way on its side.

## Console or container

`dotnet run --project src/Pulaski.Edge`, or run the published program directly. Ctrl+C (or `docker stop`) stops it the same way
a service manager does.
