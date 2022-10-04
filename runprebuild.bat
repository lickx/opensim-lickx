@echo OFF

bin\Prebuild.exe /target vs2022 /targetframework net6_0 /excludedir = "obj | bin" /file prebuild.xml

    @echo Creating compile.bat
rem To compile in debug mode
    @echo dotnet build --configuration Release OpenSim.sln > compile.bat
rem To compile in release mode comment line (add rem to start) above and uncomment next (remove rem)
rem    @echo %ValueValue% /p:Configuration=Release opensim.sln > compile.bat
:done
if exist "bin\addin-db-002" (
	del /F/Q/S bin\addin-db-002 > NUL
	rmdir /Q/S bin\addin-db-002
	)