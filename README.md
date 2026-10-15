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

Al ejecutar la instalación (requiere privilegios de administrador):
- Instala la aplicación completa en `Archivos de programa\Video Screensaver`.
- Instala un pequeño lanzador como `%WINDIR%\System32\VideoScreensaver.scr`. Windows solo detecta los `.scr` ubicados directamente en `System32`, así que esto es lo que hace que "Video Screensaver" aparezca en el desplegable del diálogo clásico "Cambiar protector de pantalla"; el lanzador simplemente reenvía la ejecución a la aplicación real.
- Registra el protector en `HKCU\Control Panel\Desktop\SCRNSAVE.EXE`.
- Abre directamente la aplicación para que puedas explorar la galería, seleccionar vídeos y probar el salvapantallas.

## Vídeo y licencias

El proyecto no incorpora vídeos. Usa material propio o con una licencia que permita el uso que quieras dar. Para una compatibilidad amplia, prefiere MP4 codificado con H.264 y sin audio o con AAC.

## Licencia

Distribuido bajo la [licencia MIT](LICENSE).

## Fiabilidad y caché

- Los vídeos que fallan se omiten durante la sesión. Si todos fallan, se reintentan tras 30 segundos, sin un bucle continuo de descargas o decodificación.
- El guardado automático agrupa los cambios durante 400 ms y escribe fuera del hilo de interfaz. Al probar el salvapantallas o cerrar la configuración se espera al guardado; un error conserva los cambios en memoria y muestra un aviso.
- La galería local cancela cargas anteriores y procesa hasta cuatro miniaturas a la vez. Las rutas escritas manualmente también se guardan.
- La caché de vídeo se mantiene al iniciar y cada hora, incluso en modo `/s`: elimina entradas sin uso durante siete días y respeta una cuota de 2048 MiB. Cada descarga tiene un máximo de 512 MiB y cinco minutos.
- Puedes ajustar `CacheLimitMb` (64–102400 MiB) y `MaxDownloadMb` (16 MiB hasta la cuota) en `%LOCALAPPDATA%\VideoScreensaver\settings.json`, con la aplicación cerrada. Los vídeos en uso se conservan si Windows impide eliminarlos; si no queda espacio, se rechaza la descarga.

## Pruebas

```powershell
dotnet test .\VideoScreensaver.sln -p:Platform=x64
```

Las pruebas cubren configuración y protección de claves, selección de reproducción y recuperación de fallos, temporización de transiciones, cuotas, descargas simultáneas, cancelación y limpieza de archivos parciales. La calidad visual de las transiciones y los códecs requiere además comprobación con vídeos reales.

## Coordinación de reproducción

`PlaybackCoordinator` controla los estados `Idle`, `Loading`, `Priming`, `Ready`, `Starting`, `Transitioning`, `Playing`, `WaitingForRetry` y `Closed`, sin depender de WinUI ni de los decodificadores. `ScreenSaverWindow` conserva la presentación, los eventos de Windows y las operaciones de los reproductores.

Cada carga tiene un identificador y un token de cancelación. Las respuestas de cargas anteriores y los eventos tardíos de un reproductor reutilizado no pueden avanzar la reproducción actual. Un fallo del vídeo entrante conserva o restaura el vídeo saliente. El cierre invalida las operaciones antes de cancelarlas y espera a que se desuscriban los eventos antes de liberar los reproductores.

Las pruebas del coordinador incluyen fallos al cargar, reanudar y hacer crossfade; fin anticipado durante una transición; cancelación; recuperación tras agotar los candidatos; y cierre desde cada estado. Las esperas de apertura y avance del vídeo tienen plazo y cancelación; solo queda el temporizador que comprueba cuándo iniciar una transición, que reduce su frecuencia durante la espera de reintento.
