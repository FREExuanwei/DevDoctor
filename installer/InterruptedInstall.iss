// Read only small, bounded installer-generated candidates. Unknown files stay in place.
#include "PayloadFingerprints.iss"

function IsInnoTemporaryName(const Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  if (Length(Name) <> 17) or (Copy(Name, 1, 3) <> 'is-') or
     (Copy(Name, 14, 4) <> '.tmp') then Exit;
  for I := 4 to 13 do
    if Pos(Name[I], 'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789') = 0 then Exit;
  Result := True;
end;

function IsOrdinaryDirectory(const Path: String): Boolean;
var
  Found: TFindRec;
begin
  Result := True;
  if FindFirst(Path, Found) then
  try
    Result := (Found.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0) and
      (Found.Attributes and FILE_ATTRIBUTE_REPARSE_POINT = 0);
  finally
    FindClose(Found);
  end;
end;

function HasIncompleteDevDoctorLog: Boolean;
var
  Found: TFindRec;
  Bytes: Int64;
begin
  Result := False;
  if FindFirst(ExpandConstant('{app}\.devdoctor-uninstall\unins*.dat'), Found) then
  try
    repeat
      if (Found.Attributes and (FILE_ATTRIBUTE_DIRECTORY or FILE_ATTRIBUTE_REPARSE_POINT) = 0) and
         FileSize64(ExpandConstant('{app}\.devdoctor-uninstall\') + Found.Name, Bytes) and (Bytes = 0) then
      begin
        Result := True;
        Exit;
      end;
    until not FindNext(Found);
  finally
    FindClose(Found);
  end;
end;

function PreserveRecognizedInstallTemps: String;
var
  Found: TFindRec;
  Source, Destination, Quarantine: String;
  Bytes: Int64;
  Data: AnsiString;
begin
  Result := '';
  if not IsOrdinaryDirectory(ExpandConstant('{app}')) or
     not IsOrdinaryDirectory(ExpandConstant('{app}\.devdoctor-uninstall')) or
     not IsOrdinaryDirectory(ExpandConstant('{app}\.devdoctor-uninstall\interrupted-files')) then
  begin
    Result := '安装修复目录不是普通本地目录，可能包含目录链接。请选择单独的安装文件夹。用户数据未被更改。';
    Exit;
  end;
  if not HasIncompleteDevDoctorLog then Exit;
  Quarantine := ExpandConstant('{app}\.devdoctor-uninstall\interrupted-files');
  if FindFirst(ExpandConstant('{app}\is-*.tmp'), Found) then
  try
    repeat
      if IsInnoTemporaryName(Found.Name) and
         (Found.Attributes and (FILE_ATTRIBUTE_DIRECTORY or FILE_ATTRIBUTE_REPARSE_POINT) = 0) then
      begin
        Source := ExpandConstant('{app}\') + Found.Name;
        if FileSize64(Source, Bytes) and (Bytes >= 65536) and (Bytes <= 67108864) then
          if LoadStringFromFile(Source, Data) then
            if IsShippedPayloadPrefix(Bytes, Lowercase(GetSHA256OfString(Copy(Data, 1, 65536)))) then
            begin
              Destination := AddBackslash(Quarantine) + Found.Name;
              if not ForceDirectories(Quarantine) or FileExists(Destination) or not RenameFile(Source, Destination) then
              begin
                Result := '先前中断的 DevDoctor 程序文件仍被占用，或无法安全保留。请关闭应用后重试。用户数据未被更改。';
                Exit;
              end;
              Log('Preserved recognized interrupted payload for owned uninstall cleanup: ' + Found.Name);
            end;
        Data := '';
      end;
    until not FindNext(Found);
  finally
    FindClose(Found);
  end;
end;
