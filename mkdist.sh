#!/bin/bash
GIT_REV=`git rev-parse --short=8 HEAD`
GIT_BRANCH=`git rev-parse --symbolic-full-name --abbrev-ref HEAD`
CUR_DATE=`date --rfc-3339='date'`
LAST_COMMIT_DATE=`git log -1 --format=%cs`

# Extended version info:
echo "${GIT_BRANCH}@${GIT_REV} (${LAST_COMMIT_DATE})" > bin/.version
# or just ommit extended version info:
#rm -f bin/.version

EXCLUDES="*.mdb *.pdb *.dll.so *Tests* *Gloebit* *OpenSimMutelist*"
TARGETZIP=opensim-${GIT_BRANCH}-${LAST_COMMIT_DATE}_${GIT_REV}.zip
echo "${TARGETZIP}"
zip -r -o ${TARGETZIP} bin CONTRIBUTORS.txt LICENSE.txt README.md ThirdPartyLicenses -x ${EXCLUDES}

#Make seperate zip for OpenSimMutelist addon:
if [ -f bin/OpenSimMutelist.Modules.dll ]; then
    TARGETZIP=opensimmutelist-${GIT_BRANCH}-${LAST_COMMIT_DATE}_${GIT_REV}.zip
    echo "${TARGETZIP}"
    zip -r -o ${TARGETZIP} bin/OpenSimMutelist.dll
fi

#Make seperate zip for Gloebit addon:
if [ -f bin/Gloebit.dll ]; then
    TARGETZIP=gloebit-${GIT_BRANCH}-${LAST_COMMIT_DATE}_${GIT_REV}.zip
    echo "${TARGETZIP}"
    cp addon-modules/Gloebit/GloebitMoneyModule/Gloebit.ini.example bin/Gloebit.ini.example
    zip -r -o ${TARGETZIP} bin/Gloebit.dll bin/Gloebit.ini.example
fi
