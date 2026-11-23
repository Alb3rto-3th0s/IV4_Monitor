# Visor IV4 – Posicionamiento de pieza

Interfaz para operador: muestra la **imagen de referencia del programa activo** junto al
**visor web de la cámara KEYENCE IV4-500CA** y, opcionalmente, flechas de corrección X/Y
leídas del **PLC Allen-Bradley** (solo lectura).

**Idioma:** inglés por defecto; botón **ES / EN** en el encabezado (o ⚙ → *Language*) para
cambiar a español en vivo. Se guarda en `config.json` (`"Language": "en" | "es"`).
Textos en `Lang\en.xaml` y `Lang\es.xaml` (mismas claves en ambos).

```
┌──────────── PROGRAMA 07 · Nombre ─────────── PLC: OK  CÁMARA: OK  12:00:00  ⚙ ┐
│  REFERENCIA (imagen cargada)      │   CÁMARA IV4 EN VIVO (visor web)           │
├───────────────────────────────────────────────────────────────────────────────┤
│  ▲ ◀ ✓ ▶ ▼   MOVER PIEZA HACIA ◀ X−          X: +2.35 mm  Y: +0.10 mm          │
└───────────────────────────────────────────────────────────────────────────────┘
```

## Arquitectura
- La **cámara** solo la controla el PLC (cambio de programa, triggers, resultados).
- La **PC** solo abre el visor web de la IV4 (WebView2) → no envía comandos a la cámara.
- El **PLC** es la fuente del programa activo y de X/Y. La app lee tags por EtherNet/IP (libplctag).
- Si el PLC está deshabilitado en la configuración, el programa se elige manualmente en el encabezado.

**Ventana:** título *Ethos - IV4 Viewer*. Al **maximizar** se oculta la barra de título de Windows y la
app ocupa toda la pantalla; al llevar el cursor al **borde superior** aparece una barra con el título y
los botones minimizar / restaurar / cerrar (doble clic en ella = restaurar). En modo kiosco no aparece.

## Varias cámaras (4 IV4)
El sistema tiene 4 cámaras similares; **cada PC se conecta a una sola**. En ⚙ → *IV4 cameras*:
- *Camera connected to this PC*: elegir cuál de las 4 usa esta estación.
- Cada cámara tiene su **nombre, URL del visor, tags del PLC** (programa, offset X, offset Y),
  **imágenes de referencia** (`References\Cam1…Cam4`) y **nombres de programa**.
- La conexión al PLC (IP, tipo, ruta, tipos de dato, tolerancias) es común a las 4.
- En *Reference images per program* → *Camera:* se pueden preparar las imágenes de cualquier
  cámara, aunque no sea la activa (útil para copiar la misma configuración a las 4 PCs).
- El nombre de la cámara activa aparece en el título del panel de cámara.
- Al actualizar desde la versión de 1 cámara, la configuración anterior pasa a la cámara 1
  (conserva su URL, tags y carpeta `Referencias`).

## Instalación en la PC de estación
1. Copiar la carpeta `publish\` a `C:\VisorIV4\`.
2. Verificar **Microsoft Edge WebView2 Runtime** (viene en Windows 11).
3. Crear acceso directo de `VisorIV4.exe` en
   `shell:startup` (o en el Programador de tareas) para arranque automático.
4. Primera ejecución → ⚙ (contraseña inicial **1234**, cámbiela) → configurar URL, PLC y referencias.

Datos de la app: `C:\ProgramData\VisorIV4\`
- `config.json` – configuración
- `References\Cam1…Cam4\P01.png … P20.png` – imágenes de referencia por cámara (se cargan desde ⚙)
- `Logs\AAAA-MM-DD.log` – eventos y errores

## Tags sugeridos en el PLC (Controller tags, uno por cámara `n` = 1…4)
| Tag | Tipo | Contenido |
|---|---|---|
| `IV4_n_Program` | DINT | Programa activo (1–20). Si maneja 0–19 usar *Offset programa* = 1 |
| `IV4_n_OffsetX` | REAL | Desplazamiento X medido − posición correcta (mm) |
| `IV4_n_OffsetY` | REAL | Desplazamiento Y medido − posición correcta (mm) |

- Los nombres son editables por cámara en ⚙; los de arriba son los valores por defecto.
- Usar **controller-scope** (para program-scope: `Program:MainProgram.IV4_1_OffsetX`).
- CompactLogix/ControlLogix: Tipo `ControlLogix`, ruta `1,0` (backplane, slot del CPU).
- Micro800: Tipo `Micro800`, ruta vacía.
- Convención de flechas: X+ = derecha, Y+ = arriba en pantalla. Si al probar sale al revés,
  marcar *Invertir eje X/Y*. Pieza desplazada hacia X+ ⇒ la app indica **mover hacia X−**.
- Recomendación: que el PLC ponga los offsets en 0 (o congele el último) cuando no haya pieza,
  para no mostrar flechas con datos viejos.

## Visor de cámara: imagen directa (recomendado) o página web
Funciona con **IV4-H** (probado con IV4-H600CA) y con **IV4 estándar** (probado con IV4-500CA).
En IV-Smart Navigator:
*Sensor Advanced → Utility → Communication Settings → Web monitor Settings → Enable(HTTP)*
(el sensor se reinicia). Manual IV4 / IV4-H, pág. 7-35. Si el menú no aparece, actualizar el
firmware del sensor.

- **Direct image (fast)** – por defecto. La app pide `http://IP/iliveimage.jpg` en ciclo por una
  sola conexión: medido ≈ 3.5 imágenes/s (la página de KEYENCE solo refresca cada 1–2 s).
  En ⚙ basta con poner `http://IP/` como *Viewer URL*; los fps se ven en el título del panel.
- **Web page** – muestra la página de KEYENCE: `http://IP/iv4-h-wm-i.html` (solo imagen) o
  `http://IP/iv4-h-wm.html` (imagen + información). En IV4 estándar las rutas no llevan `-h`:
  `iv4-wm-i.html`, `iv4-wm.html`. Evitar `iv4-h-wa.html` / `iv4-wa.html`: permiten cambiar
  programa y eso lo controla el PLC.
- **◐ Ghost Mode / Modo fantasma** (encabezado): oculta el panel de referencia y dibuja la
  referencia del programa activo (de la cámara seleccionada) semitransparente sobre la imagen en
  vivo, aunque la cámara esté sin conexión. Apagado = vista normal lado a lado. Se recuerda.
  Estilo en ⚙ → *Ghost style*: **Watermark** (por defecto: la referencia en gris, semitransparente,
  como marca de agua; el live conserva su color), **Transparent image** (la referencia a color) u
  **Outline** (solo los bordes en verde). La transparencia se ajusta solo en ⚙ → *Ghost opacity (%)*.

**Tema:** ⚙ → *Display* → *Theme*: **Dark** (por defecto) o **Light** (fondo blanco). Colores en
`Themes\Dark.xaml` y `Themes\Light.xaml` (mismas claves); se aplica al guardar. Opacidad en ⚙ → *Ghost opacity (%)*
  (5–95, por defecto 40). Solo en modo imagen directa. La referencia debe tener el mismo encuadre
  y proporción (4:3) que la imagen en vivo para que coincida.
- El Web monitor admite **máx. 2 conexiones simultáneas**; no dejar la página abierta en otro
  navegador mientras la app corre.

## Visor web de la IV4 (modo Web page)
- Confirmar en la cámara que la función de monitor web esté habilitada y anotar la URL exacta
  de cada cámara (ej. `http://192.168.0.10/`). Pegarla en ⚙ → *IV4 cameras* → *Viewer URL*.
- *Zoom* ajusta el tamaño de la página dentro del panel; *Recargar cada (min)* sirve si el
  stream se congela después de horas.
- Cada 5 s la app hace un ping TCP al puerto web; si la cámara no responde, oculta el visor,
  muestra "La cámara no responde" y reintenta solo.

## Compilar
Requiere .NET 8 SDK. Compilar desde una ruta corta (p. ej. `C:\Dev\VisorIV4`): con rutas muy
largas el compilador XAML falla al publicar para `win-x64` (error CS5001 / InitializeComponent).
```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o publish
```
