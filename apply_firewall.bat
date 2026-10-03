@echo off
echo Adding Portal Windows firewall rules for UAC (consent.exe) and port 29170...
netsh advfirewall firewall add rule name="Portal Win - TCP - in - Consent Rule - 29170+5353" dir=in action=allow protocol=TCP localport=29170,5353 program="C:\Windows\System32\consent.exe" profile=any
netsh advfirewall firewall add rule name="Portal Win - UDP - in - Consent Rule - 29170+5353" dir=in action=allow protocol=UDP localport=29170,5353 program="C:\Windows\System32\consent.exe" profile=any
netsh advfirewall firewall add rule name="Portal Win - TCP - in - Port 29170" dir=in action=allow protocol=TCP localport=29170 profile=any
echo Done!
pause
