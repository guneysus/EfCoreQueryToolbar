function _clean {
  dotnet clean
  Remove-Item ../../dist -Recurse -Force
}

function Build {
  dotnet build /p:Configuration=Release
}

function Pack {
  _clean
  Build
  dotnet pack --no-build --configuration Release
}


function Publish {
	Pack
  Get-ChildItem -Filter ../../dist/* `
  | Where-Object { $_.Extension -eq ".nupkg" -or $_.Extension -eq ".snupkg" } `
  | ForEach-Object { dotnet nuget push $_ --source https://www.myget.org/F/guneysu/api/v2/package --api-key=$env:MYGET_API_KEY }
}