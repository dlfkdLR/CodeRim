#!/usr/bin/env python3
"""Deterministically generate the dependency-free native iPhone Xcode project."""
import hashlib
import json
from pathlib import Path

root = Path(__file__).resolve().parent
objects = {}
def identifier(name): return hashlib.sha1(name.encode()).hexdigest()[:24].upper()
def add(key_name, isa, **fields):
    key = identifier(key_name); objects[key] = dict(isa=isa, **fields); return key

def plist(value, level=0):
    if isinstance(value, dict):
        return '{\n' + ''.join('\t'*(level+1)+json.dumps(k)+' = '+plist(v,level+1)+';\n' for k,v in value.items()) + '\t'*level+'}'
    if isinstance(value, list): return '(' + ', '.join(plist(v,level+1) for v in value) + ')'
    return json.dumps(str(value))

shared = ['../Sources/CodeRimShared/'+name+'.swift' for name in ['MobileModels', 'MobileRelayClient', 'MobileCredentialStore']]
attributes = 'CodeRimLiveActivity/CodeRimActivityAttributes.swift'
views = 'CodeRimLiveActivity/CodeRimLiveViews.swift'
intent = 'CodeRimLiveActivity/IslandNavigationIntent.swift'
target_sources = {
    'CodeRimMobile': shared + [attributes, views, intent] + ['CodeRimMobile/'+name+'.swift' for name in ['CodeRimMobileApp','MobileAppModel','MobileSettingsView','MobileProviderSelection','DebugPreviewView']],
    'CodeRimLiveActivity': shared + [attributes, views, intent, 'CodeRimLiveActivity/CodeRimLiveActivity.swift', 'CodeRimMobile/DebugPreviewView.swift'],
    'CodeRimMobileTests': ['CodeRimMobileTests/MobileContractTests.swift', 'CodeRimMobileTests/MobileLifecycleTests.swift'],
    'CodeRimMobileUITests': ['CodeRimMobileUITests/MobileUITests.swift'],
}
files = {}
for source in sorted(set(sum(target_sources.values(), []))):
    files[source] = add('file:'+source, 'PBXFileReference', lastKnownFileType='sourcecode.swift', path=source, sourceTree='SOURCE_ROOT')
resources = {}
for source, kind in [('CodeRimMobile/Assets.xcassets', 'folder.assetcatalog'), ('CodeRimMobile/PrivacyInfo.xcprivacy', 'text.xml')]:
    resources[source] = add('file:'+source, 'PBXFileReference', lastKnownFileType=kind, path=source, sourceTree='SOURCE_ROOT')
products = {}
for name, ext, kind in [('CodeRimMobile','app','wrapper.application'), ('CodeRimLiveActivity','appex','wrapper.app-extension'), ('CodeRimMobileTests','xctest','wrapper.cfbundle'), ('CodeRimMobileUITests','xctest','wrapper.cfbundle')]:
    products[name] = add('product:'+name, 'PBXFileReference', explicitFileType=kind, path=name+'.'+ext, sourceTree='BUILT_PRODUCTS_DIR')
products_group = add('products', 'PBXGroup', children=list(products.values()), name='Products', sourceTree='<group>')
main_group = add('main', 'PBXGroup', children=list(files.values())+list(resources.values())+[products_group], sourceTree='<group>')
project_configs=[]
for config in ['Debug','Release']:
    project_configs.append(add('project:'+config,'XCBuildConfiguration',name=config,buildSettings={
        'SDKROOT':'iphoneos','IPHONEOS_DEPLOYMENT_TARGET':'17.2','SWIFT_VERSION':'6.0',
        'SWIFT_STRICT_CONCURRENCY':'complete','TARGETED_DEVICE_FAMILY':'1','CLANG_ENABLE_MODULES':'YES',
        'SWIFT_OPTIMIZATION_LEVEL':'-Onone' if config=='Debug' else '-O',
        'SWIFT_ACTIVE_COMPILATION_CONDITIONS':'DEBUG' if config=='Debug' else '',
        'ENABLE_TESTABILITY':'YES' if config=='Debug' else 'NO',
        'DEBUG_INFORMATION_FORMAT':'dwarf' if config=='Debug' else 'dwarf-with-dsym',
        'CODERIM_RELAY_URL':'', 'CODE_SIGN_STYLE':'Automatic', 'DEVELOPMENT_TEAM':'',
    }))
project_configlist=add('project-configs','XCConfigurationList',buildConfigurations=project_configs,defaultConfigurationIsVisible='0',defaultConfigurationName='Release')
for name, sources in target_sources.items():
    source_phase=add('sources:'+name,'PBXSourcesBuildPhase',buildActionMask='2147483647',files=[add(name+':'+s,'PBXBuildFile',fileRef=files[s]) for s in sources],runOnlyForDeploymentPostprocessing='0')
    resources_phase=add('resources:'+name,'PBXResourcesBuildPhase',buildActionMask='2147483647',files=[add('resource:'+source,'PBXBuildFile',fileRef=ref) for source, ref in resources.items()] if name=='CodeRimMobile' else [],runOnlyForDeploymentPostprocessing='0')
    phases=[source_phase,resources_phase]; dependencies=[]
    if name=='CodeRimMobile':
        embed=add('embed-widget','PBXCopyFilesBuildPhase',buildActionMask='2147483647',dstPath='',dstSubfolderSpec='13',files=[add('embed-widget-file','PBXBuildFile',fileRef=products['CodeRimLiveActivity'],settings={'ATTRIBUTES':['RemoveHeadersOnCopy']})],name='Embed App Extensions',runOnlyForDeploymentPostprocessing='0')
        phases.append(embed)
    dependency = 'CodeRimLiveActivity' if name=='CodeRimMobile' else 'CodeRimMobile' if name.endswith('Tests') else None
    if dependency:
        proxy=add('proxy:'+name,'PBXContainerItemProxy',containerPortal=identifier('project'),proxyType='1',remoteGlobalIDString=identifier('target:'+dependency),remoteInfo=dependency)
        dependencies=[add('dependency:'+name,'PBXTargetDependency',target=identifier('target:'+dependency),targetProxy=proxy)]
    configs=[]
    for config in ['Debug','Release']:
        settings={'PRODUCT_NAME':name,'PRODUCT_BUNDLE_IDENTIFIER':'dev.coderim.mobile'+('.liveactivity' if name=='CodeRimLiveActivity' else '.uitests' if name.endswith('UITests') else '.tests' if name.endswith('Tests') else ''),
                  'MARKETING_VERSION':'1.0.0','CURRENT_PROJECT_VERSION':'1','SUPPORTED_PLATFORMS':'iphoneos iphonesimulator','SUPPORTS_MACCATALYST':'NO',
                  'LD_RUNPATH_SEARCH_PATHS':['$(inherited)','@executable_path/Frameworks'], 'GENERATE_INFOPLIST_FILE':'NO'}
        if name.endswith('UITests'):
            settings.update(GENERATE_INFOPLIST_FILE='YES', TEST_TARGET_NAME='CodeRimMobile')
        elif name.endswith('Tests'):
            settings.update(GENERATE_INFOPLIST_FILE='YES',TEST_HOST='$(BUILT_PRODUCTS_DIR)/CodeRimMobile.app/CodeRimMobile',BUNDLE_LOADER='$(TEST_HOST)')
        else: settings['INFOPLIST_FILE']='Config/'+name+'-Info.plist'
        if name=='CodeRimMobile':
            settings.update(ASSETCATALOG_COMPILER_APPICON_NAME='AppIcon',CODE_SIGN_ENTITLEMENTS='Config/CodeRimMobile.entitlements',APS_ENVIRONMENT='development' if config=='Debug' else 'production')
        if name=='CodeRimLiveActivity':
            settings.update(APPLICATION_EXTENSION_API_ONLY='YES',SKIP_INSTALL='YES',LD_RUNPATH_SEARCH_PATHS=['$(inherited)','@executable_path/Frameworks','@executable_path/../../Frameworks'])
        configs.append(add(name+':config:'+config,'XCBuildConfiguration',name=config,buildSettings=settings))
    configs=add(name+':configs','XCConfigurationList',buildConfigurations=configs,defaultConfigurationIsVisible='0',defaultConfigurationName='Release')
    add('target:'+name,'PBXNativeTarget',name=name,productName=name,productReference=products[name],productType='com.apple.product-type.application' if name=='CodeRimMobile' else 'com.apple.product-type.app-extension' if name=='CodeRimLiveActivity' else 'com.apple.product-type.bundle.ui-testing' if name.endswith('UITests') else 'com.apple.product-type.bundle.unit-test',buildConfigurationList=configs,buildPhases=phases,buildRules=[],dependencies=dependencies)
add('project','PBXProject',attributes={'LastUpgradeCheck':'2700','BuildIndependentTargetsInParallel':'YES'},buildConfigurationList=project_configlist,compatibilityVersion='Xcode 14.0',developmentRegion='en',knownRegions=['en','Base'],mainGroup=main_group,productRefGroup=products_group,projectDirPath='',projectRoot='',targets=[identifier('target:'+name) for name in target_sources])
project={'archiveVersion':'1','classes':{},'objectVersion':'56','objects':objects,'rootObject':identifier('project')}
(root/'CodeRimMobile.xcodeproj/project.pbxproj').write_text('// !$*UTF8*$!\n'+plist(project)+'\n')
ref=lambda name: f'<BuildableReference BuildableIdentifier="primary" BlueprintIdentifier="{identifier("target:"+name)}" BuildableName="{name}.{"app" if name=="CodeRimMobile" else "xctest"}" BlueprintName="{name}" ReferencedContainer="container:CodeRimMobile.xcodeproj"/>'
scheme=f'''<?xml version="1.0" encoding="UTF-8"?>
<Scheme LastUpgradeVersion="2700" version="1.3">
<BuildAction parallelizeBuildables="YES" buildImplicitDependencies="YES"><BuildActionEntries><BuildActionEntry buildForTesting="YES" buildForRunning="YES" buildForProfiling="YES" buildForArchiving="YES" buildForAnalyzing="YES">{ref('CodeRimMobile')}</BuildActionEntry></BuildActionEntries></BuildAction>
<TestAction buildConfiguration="Debug" selectedDebuggerIdentifier="Xcode.DebuggerFoundation.Debugger.LLDB" selectedLauncherIdentifier="Xcode.IDEFoundation.Launcher.LLDB" shouldUseLaunchSchemeArgsEnv="YES"><Testables><TestableReference skipped="NO">{ref('CodeRimMobileTests')}</TestableReference><TestableReference skipped="NO">{ref('CodeRimMobileUITests')}</TestableReference></Testables></TestAction>
<LaunchAction buildConfiguration="Debug" selectedDebuggerIdentifier="Xcode.DebuggerFoundation.Debugger.LLDB" selectedLauncherIdentifier="Xcode.IDEFoundation.Launcher.LLDB" launchStyle="0" useCustomWorkingDirectory="NO" ignoresPersistentStateOnLaunch="NO" debugDocumentVersioning="YES" debugServiceExtension="internal" allowLocationSimulation="YES"><BuildableProductRunnable runnableDebuggingMode="0">{ref('CodeRimMobile')}</BuildableProductRunnable></LaunchAction>
<ProfileAction buildConfiguration="Release" shouldUseLaunchSchemeArgsEnv="YES" savedToolIdentifier="" useCustomWorkingDirectory="NO" debugDocumentVersioning="YES"><BuildableProductRunnable runnableDebuggingMode="0">{ref('CodeRimMobile')}</BuildableProductRunnable></ProfileAction>
<AnalyzeAction buildConfiguration="Debug"/><ArchiveAction buildConfiguration="Release" revealArchiveInOrganizer="YES"/>
</Scheme>'''
(root/'CodeRimMobile.xcodeproj/xcshareddata/xcschemes/CodeRimMobile.xcscheme').write_text(scheme+'\n')
print('Generated CodeRimMobile.xcodeproj (app, Live Activity extension, contract tests)')
