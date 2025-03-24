@echo off
SET VERSION_FILE=bin\.version

FOR /F "usebackq tokens=*" %%F IN (`"git rev-parse --short=8 HEAD"`) DO (
SET GIT_REV=%%F
)
FOR /F "usebackq tokens=*" %%F IN (`"git rev-parse --symbolic-full-name --abbrev-ref HEAD"`) DO (
SET GIT_BRANCH=%%F
)
FOR /F "usebackq tokens=*" %%F IN (`"git log -1 --format=%%cs"`) DO (
SET LAST_COMMIT_DATE=%%F
)

REM Extended version info:
echo %GIT_BRANCH%@%GIT_REV% (%LAST_COMMIT_DATE%) > %VERSION_FILE%
REM or just ommit extended version info:
rem if exist %VERSION_FILE% del %VERSION_FILE%

SET TARGET_ZIP=opensim-%GIT_BRANCH%-%LAST_COMMIT_DATE%_%GIT_REV%.zip
echo %TARGET_ZIP%
set excludes=-x "*Tests*" "bin/Gloebit.*" "bin/OpenSimMutelist.Modules.*" "bin/MoneyServer.*" "OpenSim.Data.MySQL.MySQLMoneyDataWrapper.*" "OpenSim.Modules.Currency.dll.*" "OpenSim.ini.sample" "server_cert.p12" "SineWaveCert.pfx"
zip -r -o "%TARGET_ZIP%" bin CONTRIBUTORS.txt LICENSE.txt README.md ThirdPartyLicenses %excludes%

if not exist bin\OpenSimMutelist.Modules.dll goto skipmutelist
SET TARGET_ZIP=opensimmutelist-%GIT_BRANCH%-%LAST_COMMIT_DATE%_%GIT_REV%.zip
set excludes=-x "*Tests*"
echo %TARGET_ZIP%
zip -r -o "%TARGET_ZIP%" bin/OpenSimMutelist.Modules.* %excludes%
:skipmutelist

if not exist bin\Gloebit.dll goto skipgloebit
SET TARGET_ZIP=gloebit-%GIT_BRANCH%-%LAST_COMMIT_DATE%_%GIT_REV%.zip
set excludes=-x "*Tests*"
echo %TARGET_ZIP%
copy addon-modules\Gloebit\GloebitMoneyModule\Gloebit.ini.example bin\Gloebit.ini.example
zip -r -o "%TARGET_ZIP%" bin/Gloebit.* %excludes%
:skipgloebit

if not exist bin\MoneyServer.dll goto skipmoneyserver
SET TARGET_ZIP=moneyserver-%GIT_BRANCH%-%LAST_COMMIT_DATE%_%GIT_REV%.zip
set excludes=-x "*Tests*"
echo %TARGET_ZIP%
zip -r -o "%TARGET_ZIP%" bin/MoneyServer.* bin/OpenSim.Data.MySQL.MySQLMoneyDataWrapper.* bin/OpenSim.Modules.Currency.* bin/OpenSim.ini.sample bin/server_cert.p12 bin/SineWaveCert.pfx %excludes%
:skipmoneyserver


:end
