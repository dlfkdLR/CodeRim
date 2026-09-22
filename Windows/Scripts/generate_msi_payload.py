#!/usr/bin/env python3
"""Deterministic WiX component identities; no executable script is harvested."""
import argparse
import hashlib
import pathlib
import uuid
import xml.etree.ElementTree as ET

NS = 'http://wixtoolset.org/schemas/v4/wxs'
ET.register_namespace('', NS)
def element(parent, tag, **attributes):
    return ET.SubElement(parent, '{'+NS+'}'+tag, attributes)
def main():
    p=argparse.ArgumentParser();p.add_argument('publish',type=pathlib.Path);p.add_argument('output',type=pathlib.Path);a=p.parse_args()
    root=ET.Element('{'+NS+'}Wix'); fragment=element(root,'Fragment'); group=element(fragment,'ComponentGroup',Id='Payload')
    directories={'': 'INSTALLFOLDER', 'bin': 'CliBinDirectory'}
    for file in sorted(a.publish.rglob('*')):
        if not file.is_file() or file.name == 'install.ps1': continue
        if file.is_symlink(): raise ValueError('Linked payload files are not allowed')
        relative=file.relative_to(a.publish).as_posix(); parent=file.relative_to(a.publish).parent.as_posix(); parent='' if parent=='.' else parent
        cursor=''
        for part in parent.split('/') if parent else []:
            child=(cursor+'/' if cursor else '')+part
            if child not in directories:
                identifier='D'+hashlib.sha256(child.encode()).hexdigest()[:24]; directory=element(fragment,'DirectoryRef',Id=directories[cursor]);element(directory,'Directory',Id=identifier,Name=part);directories[child]=identifier
            cursor=child
        identifier='F'+hashlib.sha256(relative.encode()).hexdigest()[:24]
        component=element(group,'Component',Id=identifier,Directory=directories[parent],Guid=str(uuid.uuid5(uuid.UUID('eb9a7d9d-f86c-48c3-bef3-01f30c131c2c'),relative)))
        element(component,'File',Id='AppExecutable' if relative=='CodeRim.exe' else identifier,Source=str(file.resolve()))
        element(component,'RegistryValue',Root='HKCU',Key='Software\\CodeRim\\Installer\\Files',Name=identifier,Value='1',Type='integer',KeyPath='yes')
        if parent not in ('','bin'):element(component,'RemoveFolder',Id='R'+identifier,On='uninstall')
    ET.indent(root);a.output.write_bytes(ET.tostring(root,encoding='utf-8',xml_declaration=True))
if __name__=='__main__':main()
