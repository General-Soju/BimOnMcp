@echo off
"%~dp0BimOnMcpBridge.exe" --register-clients --report "%~dp0McpRegistration.json"
echo Restart your AI apps after successful registration.
pause
