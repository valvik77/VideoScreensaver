# Video Screensaver

Un salvapantallas interactivo para Windows 10/11 construido con **WinUI 3** y Windows App SDK. Permite reproducir en bucle vídeos desde carpetas locales o explorar y buscar vídeos de alta calidad en **Pixabay**, organizándolos en listas de reproducción personalizadas.

## Características

- **Galería Dual de Vídeos**:
  - **Carpeta Local**: Exploración de archivos de vídeo locales (`.mp4`, `.mov`, `.m4v`, `.wmv`, `.avi`).
  - **Explorador de Pixabay**: Búsqueda por palabras clave, filtro por categorías temáticas (naturaleza, fondos, ciencia, lugares, animales, etc.) y paginación integrada. Requiere configurar tu propia API Key gratuita de Pixabay (ver más abajo).
- **Previsualización interactiva con Hover**:
  - Al posar el ratón sobre cualquier miniatura de la galería, el vídeo se previsualiza dinámicamente en bucle silencioso.
- **Gestión de Listas de Reproducción (Playlist)**:
  - Añade vídeos individuales o carpetas completas a tu lista de rotación activa.
  - Elimina o vacía la lista en cualquier momento.
- **Opciones de Reproducción**:
  - Reproducción secuencial en bucle o modo aleatorio (*Shuffle*).
  - Silenciado de audio o reproducción con sonido.
  - Salida instantánea a pantalla completa al mover el ratón o pulsar una tecla.
- **Compatibilidad con Windows Screensaver**:
  - Soporta el modo salvapantallas a pantalla completa (`/s`).
  - Panel de configuración Fluent y moderno (`/c`).
  - Vista previa en miniatura incrustada dentro del diálogo nativo de Windows (`/p <HWND>`).
- **Instalador Autocontenido**:
  - Generado con Inno Setup 7, sin necesidad de dependencias externas ni descargas adicionales.

## Requisitos

- Windows 10, versión 2004 (build 19041) o posterior / Windows 11.
- Visual Studio 2022 o posterior con herramientas .NET 8 / C# y Windows App SDK.

## Desarrollo

Abre `VideoScreensaver.sln` en Visual Studio y ejecuta el perfil `x64`. Desde PowerShell también puedes compilar:

```powershell
MSBuild .\VideoScreensaver.sln /restore /p:Configuration=Release /p:Platform=x64
```

## Configurar Pixabay

El explorador de Pixabay requiere una API Key propia (la app ya no incluye ninguna clave de demostración por defecto):

1. Crea una cuenta gratuita en [pixabay.com](https://pixabay.com/) y obtén tu clave en [pixabay.com/api/docs](https://pixabay.com/api/docs/).
2. En la aplicación, pulsa el botón **API Key** junto al buscador de Pixabay y pega tu clave.

## Instalar como salvapantallas

Genera el instalador autocontenido con Inno Setup 7:

```powershell
.\scripts\Build-Installer.ps1
```

El instalador se genera en `artifacts\installer\VideoScreensaver-Setup-<versión>.exe`.

Al ejecutar la instalación:
- Instala `VideoScreensaver.exe` y `VideoScreensaver.scr` en la carpeta de programas del usuario actual (`%LOCALAPPDATA%\Programs\Video Screensaver`), sin requerir privilegios de administrador.
- Registra el protector en `HKCU\Control Panel\Desktop\SCRNSAVE.EXE`.
- Abre directamente la aplicación para que puedas explorar la galería, seleccionar vídeos y probar el salvapantallas.

## Vídeo y licencias

El proyecto no incorpora vídeos. Usa material propio o con una licencia que permita el uso que quieras dar. Para una compatibilidad amplia, prefiere MP4 codificado con H.264 y sin audio o con AAC.

## Licencia

Distribuido bajo la [licencia MIT](LICENSE).
