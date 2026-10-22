# Hernani Racing 🏁

Juego de carreras para **Unity** ambientado en **Hernani (Gipuzkoa)** con datos geográficos **reales**:
la red viaria, los edificios, el relieve, el río Urumea y la toponimia proceden de **b5m** (Diputación Foral
de Gipuzkoa) y de **geoEuskadi** (Gobierno Vasco). Los sentidos de circulación, semáforos, STOP, cedas y pasos de cebra
vienen de **OpenStreetMap**. Los coches son modelos 3D libres (**Kenney Car Kit, CC0**) convertidos para Unity.

El jugador **elige 4 cruces reales** del mapa: el juego une 1 → 2 → 3 → 4 → 1 por las calles más
cortas, cierra el circuito y lanza una carrera de **varias vueltas** contra la IA. No hay vallas ni bordillos: las calles
laterales quedan abiertas y la calzada va pegada al terreno real.
También hay un modo **Paseo libre**: conduces por todo Hernani sin carrera, con tráfico y peatones que respetan
las señales.

## Novedades (versión 5): todo el término de Hernani hasta Ugaldetxo
* **Área de 10,2 × 7,2 km** (antes 3 × 3 km): todo el término municipal de Hernani desde el límite norte (Antziola, Florida,
  Akarregi) hasta **Ugaldetxo** al sur, con unos 300 m más de valle para que la GI‑3410 no termine de golpe. No llega más al sur.
* Entra también lo que queda dentro del rectángulo: **Urnieta**, **Astigarraga**, **Ergobia**, **Epele**, **Fagollaga**,
  **Ereñotzu**, Santiagomendi, Añorga y parte de Martutene, con la A‑15, la AP‑8, la AP‑1, la GI‑2132, la GI‑3410 y la GI‑3721.
* Unos **565 km de calles y carreteras** (4.921 tramos), **6.751 edificios**, unos **206.000 árboles**, 1,4 km de túneles,
  163 cruces a distinto nivel, y señalización OSM de toda el área: 30 cruces con semáforos, 256 señales STOP y de ceda el paso, 546 pasos de cebra
  y 1.221 tramos de sentido único.
* **Nuevos circuitos predefinidos**: Hernani – Urnieta (10,4 km), Hernani – Astigarraga (7,4 km) y el **Rally Urumea** (19 km):
  sube el valle por la GI‑3410 hasta Ugaldetxo y vuelve por las pistas del monte.
* El terreno se divide en **70 teselas de 1 km** (heightmap de 2 m/píxel). El selector permite alejar el zoom hasta ver toda el área, y el mapa del HUD
  cubre todo el término.

## Novedades (versión 4)
* **Selector de coche**: turismo, berlina, utilitario, furgoneta, SUV, F1 y F1 futurista, cada uno con su
  masa, potencia, frenos, agarre y velocidad punta. Además puedes elegir el **color**. Los rivales llevan el mismo coche que tú.
* **Señalización real (OSM)**: sentidos únicos (flechas en el suelo y señal de dirección prohibida), **semáforos que
  funcionan** (ciclo verde/ámbar/rojo por cruce), STOP, ceda el paso, rotondas y pasos de cebra.
* **Marcas viales**: línea central continua o discontinua (continua cerca de cruces, en curvas, puentes y túneles), líneas de borde,
  líneas de detención y de ceda, flechas de sentido y cebreados.
* **Tráfico y peatones**: coches que circulan por su carril, respetan sentidos, semáforos, STOP, cedas, prioridad en
  rotondas y pasos de cebra, y frenan detrás de otros coches (y de ti). Los peatones pasean por las aceras y cruzan
  por los pasos de cebra, y se apartan si un coche va directo hacia ellos.
* **Cámara**: nunca queda por debajo del coche ni mira hacia arriba. Al ir marcha atrás gira y ves el coche **de frente**.
  **B** (mantener) = vista trasera.
* **Mapa**: minimapa con calles, edificios y río reales. **N** = zoom sobre tu posición (unos 100 m alrededor).
* **Flechas en el suelo**: chevrones amarillos que marcan por dónde sigue el circuito (**F** para ocultarlas).

![Mapa de los circuitos predefinidos](Docs/mapa_circuitos.png)

---

## 1. Puesta en marcha (5 minutos)

Requisitos: **Unity 2021.3 LTS o posterior** (probado contra la API de 2021.3; hay ramas
`#if` para **Unity 6** y para el **nuevo Input System**). Vale Built‑in o URP.

1. En Unity Hub crea un proyecto nuevo **3D (Built‑in)** o **3D (URP)**.
2. Copia la carpeta `Assets/HernaniRacing` dentro del `Assets` de tu proyecto.
3. *(Opcional)* Copia `DataPipeline/` **junto a** `Assets/` (en la raíz del proyecto) si quieres regenerar los datos.
4. En Unity, en el menú **Hernani Racing → 1. Crear escena de juego**.
   Se crea `Assets/HernaniRacing/Scenes/Hernani.unity`, se añade a *Build Settings* y se
   registran los shaders necesarios.
5. Pulsa **Play**. Hay una pantalla de carga de unos segundos mientras se construye el mundo
   (terreno de 10,2 × 7,2 km en 70 teselas, unos 6.750 edificios, unos 206.000 árboles, calles, puentes y túneles).
   La primera carga tarda más que en la versión anterior (en un PC normal, entre 20 y 60 s).

> Si tu proyecto usa **solo** el nuevo Input System (lo habitual en Unity 6), el código lo detecta
> automáticamente. Con *Active Input Handling = Both* se usa el Input Manager clásico.

Para un ejecutable: *File → Build Settings → Build*. Si los materiales salen rosas en el build, ejecuta
**Hernani Racing → 2. Incluir shaders para builds**.

---

## 2. Cómo se juega

### Selector de circuito (vista de mapa)
* **Clic** en 4 cruces (puntos amarillos) **en el orden de carrera**. El circuito aparece al instante con:
  longitud de vuelta, desnivel acumulado, pendiente máxima, cotas, longitud de cada tramo y calles por las que pasa.
* **Retroceso** quita el último cruce; la ✕ de cada fila quita ese cruce.
* **Ratón**: arrastrar para mover y rueda para zoom · **WASD / flechas** para desplazarse, **Q / E** para el zoom y **Mayús** para ir más rápido.
* Avisos: el selector te avisa si el trazado repite calle (ida y vuelta) o tiene un giro de 180º. Cambia el orden de los cruces para evitarlo.
* Ajustes: **vueltas** (1–20), **rivales IA** (0–5) y **dificultad** (Fácil / Normal / Difícil).
* **Coche** y **color**: elige el modelo. Verás sus datos (largo, masa, velocidad punta, agarre).
* **Tráfico y peatones**: por defecto, en carrera el tráfico está **desactivado** y los peatones activados; en el paseo, ambos activados.
* Botones **Circuitos predefinidos**, **Aleatorio** y **Borrar**. Después, **¡A CORRER!**
* **PASEO LIBRE**: empieza junto al cruce 1 si lo has marcado; si no, en el centro de Hernani.

### Circuitos predefinidos
| Circuito | Cruces | Vuelta |
|---|---|---|
| GP Urumea | Centro – Karabel – Florida – Latsunbe | ≈ 3,9 km |
| Subida a Elizatxo | Portu – Elizatxo – Centro – Karabel | ≈ 2,8 km (pendientes de hasta el 24 %) |
| Vuelta Antziola | Antziola – Florida – Latsunbe – Galarreta | ≈ 4,5 km |
| Sprint Casco | Centro – Portu – Karabel – Florida | ≈ 2,9 km |
| Hernani – Urnieta | Centro – Zikuñaga – Urnieta – Osinaga | ≈ 10,4 km |
| Hernani – Astigarraga | Centro – Akarregi – Astigarraga – Florida | ≈ 7,4 km |
| Rally Urumea | Portu – Fagollaga – Ereñotzu – Ugaldetxo, con la vuelta por el monte | ≈ 19 km (sube a 354 m, con rampas muy duras) |

### Controles en carrera
| Acción | Teclado | Mando |
|---|---|---|
| Acelerar | W / ↑ | Gatillo derecho |
| Frenar / marcha atrás (mantener parado) | S / ↓ | Gatillo izquierdo |
| Girar | A D / ← → | Stick izquierdo |
| Freno de mano | Espacio | A / ✕ |
| Recolocar en el circuito | R | Y / △ |
| Cambiar cámara | C | X / □ |
| Pausa | Esc / P | Start |
| Minimapa grande | M | — |
| Zoom del mapa en tu posición (~100 m) | N | Cruceta ↑ / Back |
| Vista trasera (mantener) | B | LB / L1 |
| Mostrar / ocultar flechas del suelo | F | Cruceta ↓ |

### Paseo libre
* Sin vueltas ni rivales: puedes ir por cualquier calle del área (≈ 10 × 7 km, de Martutene a Ugaldetxo).
* El HUD muestra la calle, el barrio, las coordenadas UTM, la altitud, la velocidad y un límite orientativo. En el mapa
  aparecen los cruces con semáforo (en naranja) y los coches del tráfico.
* Te **avisa** si vas en dirección prohibida por una calle de sentido único, si te saltas un semáforo en rojo o un STOP. Solo son
  avisos, sin penalización.
* **R** te recoloca en la calle más cercana, en un sentido permitido. **Esc** abre el menú (Continuar / Volver al menú).

El HUD muestra la posición, la vuelta, los tiempos (actual, última y mejor), la velocidad, la calle y el barrio
por los que vas, y la altitud. Si vas en sentido contrario, te avisa. Al terminar verás la clasificación, con
opciones para repetir la carrera o elegir otro circuito.

### Salirse del circuito
* No hay vallas: puedes meterte por una calle lateral, una acera o un prado. El juego solo te **avisa**
  («FUERA DEL CIRCUITO»). No hay cuenta atrás ni te recoloca él solo.
* Mientras estás fuera, tu progreso **se congela** en el punto por donde saliste. Para seguir contando,
  vuelve a la pista **por donde saliste** o un poco más adelante.
* Si reapareces en el circuito **más de 100 m por delante** (un atajo), te devuelve al punto donde saliste.
* **R** te recoloca en el circuito cuando quieras. Solo te recoloca automáticamente si vuelcas o caes fuera del mapa.

### Física justa
* Todos los coches de la carrera (tú y la IA) usan el **mismo modelo**, con la misma masa, potencia, agarre y colisionadores. Nadie recibe
  ayudas: se ha quitado el *rubber band*, que aceleraba a la IA cuando iba por detrás.
* Los coches del tráfico son cinemáticos: no te empujan, pero sí chocas con ellos. Si se lo pides, apágalos en el selector.
* Las ruedas solo apoyan en el suelo, nunca sobre otro coche. Así nadie se «sube» encima de otro ni gana ventaja al chocar.
* La IA respeta a los coches que lleva delante o al lado: cambia de carril para adelantar o levanta el pie,
  en vez de empujar. En un contacto, los dos coches se empujan con la misma fuerza.

---

## 3. Datos geográficos

| Dato | Fuente | Uso en el juego |
|---|---|---|
| Viales (ejes de calles y carreteras, nombres, puentes y túneles) | b5m · `GFA_DSET_PATHW` | Red viaria, grafo de cruces, nombres de calle |
| Edificios (huella, altura media y máxima LiDAR, soportales) | b5m · `GFA_DSET_BU` | Edificios extruidos a su altura real |
| Toponimia | b5m · `GFA_DSET_TOPON` | Barrios (Centro, Karabel, Florida, Elizatxo, Portu, Antziola…), polígonos, rotondas |
| Hidrografía | b5m · `GFA_DSET_HYD` | Río Urumea y arroyos |
| Modelo digital del terreno | geoEuskadi · MDT LiDAR (WCS `WCS_KARTOGRAFIA`). Si no responde, se usa el MDT05 del IGN/IDEE | Terreno de Unity: 70 teselas de 1024 m (513² cada una), 2 m/píxel; `heightmap.bytes` comprimido |

* Área: un rectángulo de **10.240 × 7.168 m**, de E 580450 a 590690 y de N 4785950 a 4793118 (ETRS89 / UTM 30N, EPSG:25830).
  Cubre todo el término de Hernani al norte de Ugaldetxo (el término llega más al sur, hasta Goizueta, pero esa parte queda fuera),
  y además Urnieta, Astigarraga y los barrios vecinos.
* En Unity: `x = E − 580450`, `z = N − 4785950`, `y = altitud real (m)`. **1 unidad = 1 metro.**
* El terreno se ajusta bajo las calzadas y los cauces. Las pendientes de las calles se limitan a valores
  físicos para eliminar artefactos del MDT junto a muros y taludes: 24 % en calles, 12 % en carreteras y 7 % en autovías.
* **El terreno es el suelo por el que se conduce.** Las calles se modelan en el propio relieve, con una mezcla suave
  en los cruces, y el asfalto es una malla visual pegada encima. Al pasar de una calle a otra no hay escalones.
* **Puentes y túneles según b5m.** El campo `BRIDGE_TUNNEL` de los viales b5m se interpreta así:
  `3` = puente o viaducto, `4` = túnel, `2` = paso cubierto y `1` = paso inferior (la vía va a nivel; el puente es la
  otra). En versiones anteriores los códigos estaban cruzados, y de ahí salían los túneles en zanja y los muros falsos.
* **Cruces a distinto nivel**: hay 163 cruces entre vías. En todos, la vía de arriba pasa a un mínimo de 5,5 m sobre la de abajo
  (se han corregido 83), y también es puente todo lo que pasa sobre otra calle (por ejemplo, el enlace de la A‑15 sobre
  Santa Barbara). Los puentes son tableros con colisionador:
  * Empiezan a ras del terreno solo donde la vía sigue a nivel.
  * Si continúan en otro viaducto o entran en un túnel, enlazan sin escalón.
  * Los enlaces en viaducto tienen una losa de tablero propia.
* **Pretiles solo donde hay caída real**: un lado del tablero lleva pretil solo si debajo hay más de 2,5 m de
  caída (viaductos, pasos sobre otra vía o sobre un río). Ya no hay "vallas" a lo largo de la calzada.
* **Túneles de verdad** (unos 1,4 km en total: los tubos de la A‑15, Martindegi, Antziola y los de la AP‑8 / AP‑1 dentro del área):
  * **Monte:** se conserva por encima, con al menos 6,2 m sobre la calzada. Ya no hay zanjas ni arcos flotando.
  * **Agujeros:** solo en las bocas, y solo donde el monte se metería en el túnel. Unity usa *Terrain Holes*.
  * **Tubo:** tiene solera, hastiales y bóveda con colisionador, muros de boca de hormigón y luminarias en la clave.
  * **Exterior:** un cajón de hormigón tapa los bordes de los agujeros. Donde hay otra vía al lado (los tubos gemelos
    de la A‑15 en Portu), el cajón se estrecha para no asomar dentro del otro tubo.
* **Ya incluidos**: los datos procesados están en `Assets/HernaniRacing/Resources/HernaniData/` (unos 34 MB), así que **no hace falta Python** para jugar.

> **URP:** los agujeros del terreno de las bocas necesitan la opción **Terrain Holes** activada en el asset de URP
> (*Project Settings → Graphics → URP Asset → Rendering → Terrain Holes*). Viene activada por defecto. Con el
> render pipeline integrado (Built‑in) no hay que hacer nada.

### Regenerar los datos (opcional)
```bash
cd DataPipeline
pip install -r requirements.txt
python build_hernani_data.py                       # descarga b5m + MDT de geoEuskadi (o IGN)
python build_hernani_data.py --dtm mi_mdt_1m.tif   # MDT LiDAR 1 m de geoEuskadi descargado a mano
python build_hernani_data.py --bounds 580450 4785950 590150 4793100   # rectángulo W S E N (es el valor por defecto)
python build_hernani_data.py --cell 1.5            # heightmap más fino (más memoria y más MB)
python build_hernani_data.py --center 583200 4791100 --size 3000      # área antigua de 3 × 3 km
python build_hernani_data.py --no-motorways        # sin A-15 / AP-1 / AP-8
python build_hernani_data.py --no-osm              # sin sentidos/señales de OpenStreetMap
python osm_traffic.py --refresh                    # solo vuelve a descargar y aplicar OSM (~30 s)
python build_cars.py                               # vuelve a convertir los coches CC0 de Kenney
python preview_map.py                              # vuelve a dibujar Docs/mapa_circuitos.png
```
También desde Unity: **Hernani Racing → 3. Regenerar datos geográficos (Python)**.
Las descargas se guardan en `DataPipeline/cache/`. El MDT LiDAR de 1 m de geoEuskadi se puede descargar en
<https://www.geo.euskadi.eus/lidar/DatosDescarga/> (`MDT_LIDAR_2017_ETRS89`).

---

## 4. Estructura del código

```
Assets/HernaniRacing/
  Resources/HernaniData/    meta.json, roads.json (con sentidos/señales OSM), buildings.json, scenery.json, heightmap.bytes
  Resources/HernaniCars/    cars.json + un JSON por coche (malla, colores, ruedas y física)
  Scripts/Runtime/
    GameBootstrap.cs        Punto de entrada: cámara, sol, niebla, carga y flujo del juego
    HernaniData.cs          Carga de los JSON (JsonUtility) y del heightmap
    WorldBuilder.cs         Terreno, asfalto pegado al terreno, aceras, puentes, túneles, edificios, río
    TreeRenderer.cs         Árboles con GPU instancing por bloques
    RoadGraph.cs            Grafo viario: cruces, Dijkstra, búsquedas espaciales
    Circuit.cs              Circuito de 4 cruces: muestreo, trazada, perfil de velocidad, seguimiento
    CircuitSelector.cs      Selector de cruces sobre el mapa (IMGUI)
    CircuitVisuals.cs       Línea de salida/meta y pórtico (sin vallas ni bordillos)
    CarController.cs        Física de coche por raycast (suspensión, agarre, derrape)
    CarModels.cs            Carga de los modelos de coche y aplicación de su física
    RoadFurniture.cs        Marcas viales, señales, semáforos (TrafficControl) y carriles
    Traffic.cs              Tráfico (TrafficCar) y peatones (Pedestrian)
    MapView.cs              Minimapa real y flechas guía en el suelo
    FreeRoam.cs             Modo paseo libre
    LowPolyCar.cs           Coches low-poly procedurales y sonido de motor sintetizado
    Drivers.cs              Piloto jugador, IA que sigue la trazada, cámara de persecución
    RaceManager.cs          Salida, vueltas, posiciones, HUD, pausa y resultados
    GameInput.cs            Teclado y mando (Input Manager clásico o nuevo Input System)
    Mat.cs, MeshBuilder.cs  Materiales (Built-in y URP) y generación de mallas
  Scripts/Editor/
    HernaniRacingMenu.cs    Menú «Hernani Racing»
DataPipeline/
  build_hernani_data.py     Descarga y procesamiento de b5m, geoEuskadi e IGN (al final llama a osm_traffic)
  osm_traffic.py            Sentidos, carriles, rotondas, semáforos, STOP/ceda y pasos de cebra desde OpenStreetMap
  build_cars.py             Conversión de los GLB CC0 de Kenney al formato JSON del juego
  preview_map.py            Mapa de control
  requirements.txt
```

Todo se genera en tiempo de ejecución (mallas, materiales, texturas de señales y sonido). Los coches se leen de JSON, porque Unity no
importa GLB sin paquetes extra. No hace falta instalar ningún paquete adicional.

---

## 5. Licencias y atribución

* **b5m** – © Diputación Foral de Gipuzkoa, <https://b5m.gipuzkoa.eus>. Datos abiertos, licencia **CC BY 4.0**.
* **geoEuskadi** – © Gobierno Vasco / Eusko Jaurlaritza, <https://www.geo.euskadi.eus>. Datos abiertos, licencia **CC BY 4.0**.
* **MDT05** – © Instituto Geográfico Nacional (CNIG / IDEE), licencia **CC BY 4.0** (scne.es).
* **OpenStreetMap** – © OpenStreetMap contributors, licencia **ODbL 1.0** (<https://www.openstreetmap.org/copyright>).
  Se usa para los sentidos de circulación, carriles, rotondas, semáforos, señales STOP / ceda y pasos de cebra.
  Los datos derivados (`roads.json`) quedan bajo ODbL.
* **Coches** – *Car Kit* y *Racing Kit* de **Kenney** (<https://kenney.nl>), licencia **CC0 1.0** (dominio público).

Los datos incluidos en este paquete se generaron con el **MDT05 del IGN** porque el servicio de geoEuskadi no
estaba accesible en ese momento (consulta `meta.json → dtmSource`). Al regenerarlos en tu equipo, el pipeline
intenta primero el MDT LiDAR de geoEuskadi.

## Subir el proyecto a GitHub

El proyecto ya trae `.gitignore` (de Unity) y `.gitattributes`, así que solo se sube lo necesario.

**Opción fácil (GitHub Desktop, https://desktop.github.com):**
1. File → **Add local repository** → elige la carpeta `HernaniRacing`.
2. Te dirá que no es un repositorio: pulsa **"create a repository"** → Create repository (rama `main`).
3. Pulsa **Publish repository** (marca "Keep this code private" si lo quieres privado).

**Opción consola (Git instalado):**
```
cd HernaniRacing
git init -b main
git add -A
git commit -m "Hernani Racing v5"
git remote add origin https://github.com/TU_USUARIO/HernaniRacing.git
git push -u origin main
```
(Antes crea el repositorio vacío en https://github.com/new, sin README ni .gitignore.)

Unity regenera solo `Library/`, `Temp/`, `Logs/` y `UserSettings/`; no se suben.
El fichero más grande (`heightmap.bytes`, 15 MB) está muy por debajo del límite de GitHub (100 MB): no hace falta Git LFS.
Datos: © b5m Gipuzkoa, © IGN (MDT05), © OpenStreetMap contributors (ODbL); coches Kenney (CC0).
