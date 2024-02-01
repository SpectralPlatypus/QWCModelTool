# QWCModelTool
WIP tool for extracting models from Harry Potter: Quidditch World Cup. This tool can extract all mesh nodes located in an FPM file and convert their textures to PNG format. The output will be a 
[glb](https://www.khronos.org/gltf/) file, but glTF can be output instead using the `--out` flag.

## Usage
`QWCModelTool.exe --file ...\file.fpm`

The file argument must point to an [extracted](https://github.com/SpectralPlatypus/QuidditchArchiveTool) FPM file. All necessary textures (.fsh) must be under the same directory.

### Other Arguments:
--nolm : Skip exporting meshes using lightmap textures

--lmalp: Specify fixed alpha value added to lightmap textures

--out: Specify outputfile location and name. By default, the exports will be placed under the same directory as the input file.

## Building and Publishing
- Clone and navigate to the root directory of the repo
- Run the following command:
  `dotnet.exe publish --configuration Release --runtime win-x64`
- Standalone binary will be located under `bin\Release\net6.0\win-x64\publish`

## Missing Features
- FSH: Mipmap extraction
- FSH: Global palette in textures (not used in QWC)
- GRO: Alpha Shader Groper file parsing
- Skinned models: .skl and .fas files require further analysis to understand how to handle skeletons
