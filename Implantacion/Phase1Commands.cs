using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using System.IO;
using System.Collections.Generic;
using Newtonsoft.Json;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Colors;
// No se usa System.Linq

namespace Civil3D_Phase1
{
    public class TrackerModel
    {
        // ... (Esta clase no cambia) ...
        public string id_tracker { get; set; }
        public string fabricante { get; set; }
        public string modelo { get; set; }
        public string configuracion { get; set; }
        public double ancho_huella_ns { get; set; }
        public double longitud_largo { get; set; }
        public double longitud_corto { get; set; }
        public string bloque_cad_largo { get; set; }
        public string bloque_cad_corto { get; set; }
    }

    public class LayoutResult
    {
        // ... (Esta clase no cambia) ...
        public double OffsetNS { get; set; }
        public int TotalTrackers { get; set; }
        public int LongTrackers { get; set; }
        public int ShortTrackers { get; set; }
        public List<Polyline> TrackersToDraw { get; set; }
    }


    public class Phase1Commands
    {
        [CommandMethod("FASE1")]
        public static void RunPhase1()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            Database db = doc.Database;

            // --- CAMBIO DE VERSIÓN ---
            ed.WriteMessage("\n--- Iniciando FASE 1 (v59 - Algoritmo Ray-Cast Corregido) ---");

            // --- PASO 1: Cargar Biblioteca de Trackers (Sin cambios) ---
            List<TrackerModel> trackerLibrary;
            string dllPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            string dllDirectory = Path.GetDirectoryName(dllPath);
            string jsonPath = Path.Combine(dllDirectory, "trackers.json");
            try
            {
                string jsonContent = File.ReadAllText(jsonPath);
                trackerLibrary = JsonConvert.DeserializeObject<List<TrackerModel>>(jsonContent);
                if (trackerLibrary == null || trackerLibrary.Count == 0) { ed.WriteMessage("\nERROR: 'trackers.json' vacío."); return; }
                ed.WriteMessage($"\nBiblioteca 'trackers.json' cargada. {trackerLibrary.Count} modelos encontrados.");
            }
            catch (System.Exception ex) { ed.WriteMessage($"\nERROR al leer 'trackers.json': {ex.Message}."); return; }


            // --- PASO 2: Solicitar Inputs de Layout (Sin cambios) ---

            // 2a. Seleccionar Tracker
            PromptKeywordOptions pkoTracker = new PromptKeywordOptions("\nSeleccione el id_tracker de la biblioteca:");
            foreach (var tracker in trackerLibrary) { pkoTracker.Keywords.Add(tracker.id_tracker); }
            pkoTracker.Keywords.Default = trackerLibrary[0].id_tracker;
            PromptResult prTracker = ed.GetKeywords(pkoTracker);
            if (prTracker.Status != PromptStatus.OK) { return; }
            string selectedTrackerId = prTracker.StringResult;
            TrackerModel selectedTracker = trackerLibrary.Find(t => t.id_tracker == selectedTrackerId);
            ed.WriteMessage($"\nTracker '{selectedTracker.id_tracker}' seleccionado.");

            // 2b. Pedir Pitch Eje-a-Eje (E-O)
            PromptDoubleOptions pdoPitch = new PromptDoubleOptions("\nIntroduzca el Pitch Eje-a-Eje (E-O) en metros:");
            pdoPitch.AllowNegative = false; pdoPitch.AllowZero = false; pdoPitch.DefaultValue = 10.0;
            PromptDoubleResult prPitch = ed.GetDouble(pdoPitch);
            if (prPitch.Status != PromptStatus.OK) { return; }
            double pitchEO = prPitch.Value;
            ed.WriteMessage($"\nPitch E-O seleccionado: {pitchEO}m");

            // 2c. Pedir Offset N-S
            PromptDoubleOptions pdoOffsetNS = new PromptDoubleOptions("\nIntroduzca el Offset (distancia libre N-S) en metros:");
            pdoOffsetNS.AllowNegative = false; pdoOffsetNS.AllowZero = true; pdoOffsetNS.DefaultValue = 0.5;
            PromptDoubleResult prOffsetNS = ed.GetDouble(pdoOffsetNS);
            if (prOffsetNS.Status != PromptStatus.OK) { return; }
            double pasoLibreNS = prOffsetNS.Value;
            ed.WriteMessage($"\nOffset N-S seleccionado: {pasoLibreNS}m");

            // 2d. Pedir Retranqueo (Setback)
            PromptDoubleOptions pdoSetback = new PromptDoubleOptions("\nIntroduzca el retranqueo (setback) de la parcela en metros:");
            pdoSetback.AllowNegative = false; pdoSetback.AllowZero = true; pdoSetback.DefaultValue = 5.0;
            PromptDoubleResult prSetback = ed.GetDouble(pdoSetback);
            if (prSetback.Status != PromptStatus.OK) { return; }
            double setback = prSetback.Value;
            ed.WriteMessage($"\nRetranqueo seleccionado: {setback}m");


            // --- PASO 3: Selección de Geometría (Sin cambios) ---
            ObjectId parcelId = SelectPolyline(ed, "\nSeleccione la Polilínea de la Parcela (CERRADA):", true);
            if (parcelId == ObjectId.Null) { return; }
            ed.WriteMessage("\nParcela cerrada seleccionada.");

            ObjectIdCollection affectionIds = SelectMultiplePolylines(db, ed, "\nSeleccione las Polilíneas de Afecciones (CERRADAS):");
            ed.WriteMessage($"\n{affectionIds.Count} afecciones CERRADAS seleccionadas.");


            // --- PASO 4: Cálculo de Área Neta (Sin cambios) ---
            ed.WriteMessage("\nIniciando Paso 4: Cálculo del Área Neta...");
            ObjectId netAreaId = GetNetArea(db, parcelId, setback);
            if (netAreaId == ObjectId.Null) { ed.WriteMessage("\nERROR: No se pudo calcular el Área Neta (retranqueo)."); return; }
            ed.WriteMessage("\nÁrea Neta (retranqueo) calculada.");


            // --- PASO 5: Generación de Layout (MODIFICADO v59) ---
            ed.WriteMessage("\nCreando capas de salida...");
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CreateLayer(db, tr, "TRACKERS_LARGOS", Color.FromRgb(0, 100, 255));
                CreateLayer(db, tr, "TRACKERS_CORTOS", Color.FromRgb(255, 100, 0));
                tr.Commit();
            }

            ed.WriteMessage("\n--- Iniciando Paso 5: Generando Layout (Método Ray-Cast v59) ---");

            LayoutResult finalLayout = RunLayout_v59(db, ed, netAreaId, affectionIds, selectedTracker, pitchEO, pasoLibreNS);

            if (finalLayout == null)
            {
                ed.WriteMessage("\nERROR: Fallo crítico durante la generación de Layout (v59).");
                return;
            }

            if (finalLayout.TotalTrackers == 0)
            {
                ed.WriteMessage("\nAVISO: No caben trackers en el área válida.");
                return;
            }

            ed.WriteMessage("--- Generación de Layout Terminada ---");
            ed.WriteMessage("\n--- LAYOUT FINAL GENERADO ---");
            ed.WriteMessage($"Offset (N-S): {finalLayout.OffsetNS:F2}m");
            ed.WriteMessage($"Total Trackers: {finalLayout.TotalTrackers}");
            ed.WriteMessage($"Trackers Largos ({selectedTracker.longitud_largo}m): {finalLayout.LongTrackers}");
            ed.WriteMessage($"Trackers Cortos ({selectedTracker.longitud_corto}m): {finalLayout.ShortTrackers}");


            // --- PASO 6: Dibujado Final (Sin cambios) ---
            ed.WriteMessage("\n--- Iniciando Paso 6: Dibujando Layout ---");
            DrawFinalLayout(db, finalLayout);
            ed.WriteMessage("\n¡Trackers dibujados con éxito!");

            ed.WriteMessage("\n--- PROCESO FASE 1 TERMINADO (v59) ---");
        }

        // --- Función Auxiliar 1 (SelectPolyline, v56) ---
        private static ObjectId SelectPolyline(Editor ed, string message, bool requireClosed)
        {
            PromptEntityOptions peo = new PromptEntityOptions(message);
            peo.SetRejectMessage("\nEl objeto seleccionado no es una Polilínea 2D o Círculo.");
            peo.AddAllowedClass(typeof(Polyline), true);
            peo.AddAllowedClass(typeof(Polyline2d), true);
            peo.AddAllowedClass(typeof(Circle), true); 

            PromptEntityResult per = ed.GetEntity(peo);
            if (per.Status == PromptStatus.OK)
            {
                if (requireClosed)
                {
                    using (Transaction tr = per.ObjectId.Database.TransactionManager.StartTransaction())
                    {
                        Curve curve = tr.GetObject(per.ObjectId, OpenMode.ForRead) as Curve;
                        if (curve != null && !curve.Closed)
                        {
                            ed.WriteMessage("\nERROR: La polilínea seleccionada debe estar CERRADA. Cancelando.");
                            tr.Abort();
                            return ObjectId.Null;
                        }
                        tr.Commit();
                    }
                }
                return per.ObjectId;
            }
            return ObjectId.Null;
        }

        // --- Función Auxiliar 2 (SelectMultiplePolylines, v56) ---
        private static ObjectIdCollection SelectMultiplePolylines(Database db, Editor ed, string message)
        {
            ObjectIdCollection finalCollection = new ObjectIdCollection();
            PromptSelectionOptions pso = new PromptSelectionOptions();
            pso.MessageForAdding = message;
            pso.MessageForRemoval = "\nEliminar objetos de la selección:";
            TypedValue[] filter = new TypedValue[]
            {
                new TypedValue((int)DxfCode.Operator, "<OR"),
                new TypedValue((int)DxfCode.Start, "POLYLINE"),
                new TypedValue((int)DxfCode.Start, "LWPOLYLINE"),
                new TypedValue((int)DxfCode.Start, "POLYLINE2D"),
                new TypedValue((int)DxfCode.Start, "CIRCLE"), // Permitir Círculos también
                new TypedValue((int)DxfCode.Operator, "OR>")
            };
            SelectionFilter selFilter = new SelectionFilter(filter);
            PromptSelectionResult psr = ed.GetSelection(pso, selFilter);

            if (psr.Status == PromptStatus.OK)
            {
                int openCount = 0;
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in psr.Value.GetObjectIds())
                    {
                        Curve curve = tr.GetObject(id, OpenMode.ForRead) as Curve;
                        if (curve != null && curve.Closed)
                        {
                            finalCollection.Add(id);
                        }
                        else
                        {
                            openCount++;
                        }
                    }
                    tr.Commit();
                }
                if (openCount > 0)
                {
                    ed.WriteMessage($"\n(Se ignoraron {openCount} polilíneas que no estaban cerradas.)");
                }
            }
            return finalCollection;
        }

        // --- Función Auxiliar 4 (CreateLayer, sin cambios) ---
        private static void CreateLayer(Database db, Transaction tr, string layerName, Color color)
        {
            LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (!lt.Has(layerName))
            {
                lt.UpgradeOpen();
                LayerTableRecord ltr = new LayerTableRecord();
                ltr.Name = layerName; ltr.Color = color;
                lt.Add(ltr);
                tr.AddNewlyCreatedDBObject(ltr, true);
            }
        }

        // --- Función Auxiliar 5 (GetNetArea, sin cambios) ---
        private static ObjectId GetNetArea(Database db, ObjectId parcelId, double setback)
        {
            if (setback == 0) return parcelId;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Curve curve = tr.GetObject(parcelId, OpenMode.ForRead) as Curve;
                if (curve == null) { return ObjectId.Null; }
                double originalArea = curve.Area;
                string layerName = "AREA_NETA";
                Color color = Color.FromRgb(255, 0, 255); // Magenta
                CreateLayer(db, tr, layerName, color);
                DBObjectCollection offsetCurves = null;
                try { offsetCurves = curve.GetOffsetCurves(-setback); } catch (System.Exception) { return ObjectId.Null; }
                if (offsetCurves != null && offsetCurves.Count > 0)
                {
                    Curve offsetCurve = offsetCurves[0] as Curve;
                    if (offsetCurve != null && offsetCurve.Area < originalArea)
                    {
                        return AddCurveToModelSpace(db, tr, offsetCurve, layerName);
                    }
                }
                try { offsetCurves = curve.GetOffsetCurves(setback); } catch (System.Exception) { return ObjectId.Null; }
                if (offsetCurves != null && offsetCurves.Count > 0)
                {
                    Curve offsetCurve = offsetCurves[0] as Curve;
                    if (offsetCurve != null && offsetCurve.Area < originalArea)
                    {
                        return AddCurveToModelSpace(db, tr, offsetCurve, layerName);
                    }
                }
                return ObjectId.Null;
            }
        }

        // --- Función Auxiliar 6 (AddCurveToModelSpace, sin cambios) ---
        private static ObjectId AddCurveToModelSpace(Database db, Transaction tr, Curve curve, string layerName)
        {
            BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
            Entity ent = curve as Entity;
            ent.Layer = layerName;
            btr.AppendEntity(ent);
            tr.AddNewlyCreatedDBObject(ent, true);
            tr.Commit();
            return ent.ObjectId;
        }


        // --- 'RunLayout_v59' (MODIFICADO v59) ---
        // Llama a las funciones de teselado v53 y colisión v59
        private static LayoutResult RunLayout_v59(Database db, Editor ed, ObjectId netAreaId, ObjectIdCollection affectionIds, TrackerModel tracker, double pitchEO, double offsetNS)
        {
            LayoutResult layout = new LayoutResult
            {
                OffsetNS = offsetNS,
                TrackersToDraw = new List<Polyline>()
            };

            List<Point2d> netAreaVertices = new List<Point2d>();
            List<List<Point2d>> affectionVerticesList = new List<List<Point2d>>();
            Extents3d totalExtents;
            
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                try
                {
                    Curve netAreaCurve = tr.GetObject(netAreaId, OpenMode.ForRead) as Curve;
                    if (netAreaCurve == null) { ed.WriteMessage("\nERROR: No se pudo leer la curva del Área Neta."); tr.Abort(); return null; }
                    totalExtents = netAreaCurve.GeometricExtents;

                    // Usamos la función de teselado que SÍ compiló (de v53)
                    netAreaVertices = GetTessellatedVertices_v53(netAreaCurve);

                    foreach (ObjectId id in affectionIds)
                    {
                        Curve affCurve = tr.GetObject(id, OpenMode.ForRead) as Curve;
                        if (affCurve != null)
                        {
                            affectionVerticesList.Add(GetTessellatedVertices_v53(affCurve));
                        }
                    }
                    
                    // --- Bucle de Layout ---
                    
                    // Bucle E-O (X) - Filas
                    for (double x = totalExtents.MinPoint.X; x < totalExtents.MaxPoint.X; x += pitchEO)
                    {
                        // Bucle N-S (Y) - Trackers
                        double y = totalExtents.MinPoint.Y;
                        while (y < totalExtents.MaxPoint.Y)
                        {
                            Point3d centerPt = new Point3d(x + (tracker.ancho_huella_ns / 2.0), y + (tracker.longitud_largo / 2.0), 0);
                            
                            // Test de Colisión (Largo) - LLAMA A LA NUEVA LÓGICA v59
                            if (IsTrackerValid_4Corners_v59(netAreaVertices, affectionVerticesList, centerPt, tracker.longitud_largo, tracker.ancho_huella_ns))
                            {
                                layout.LongTrackers++;
                                layout.TrackersToDraw.Add(CreateTrackerPolyline_NS(centerPt, tracker.longitud_largo, tracker.ancho_huella_ns, "TRACKERS_LARGOS"));
                                y += tracker.longitud_largo + offsetNS; 
                            }
                            else
                            {
                                // Test de Colisión (Corto) - LLAMA A LA NUEVA LÓGICA v59
                                if (tracker.longitud_corto > 0.01)
                                {
                                    centerPt = new Point3d(x + (tracker.ancho_huella_ns / 2.0), y + (tracker.longitud_corto / 2.0), 0);
                                    if (IsTrackerValid_4Corners_v59(netAreaVertices, affectionVerticesList, centerPt, tracker.longitud_corto, tracker.ancho_huella_ns))
                                    {
                                        layout.ShortTrackers++;
                                        layout.TrackersToDraw.Add(CreateTrackerPolyline_NS(centerPt, tracker.longitud_corto, tracker.ancho_huella_ns, "TRACKERS_CORTOS"));
                                        y += tracker.longitud_corto + offsetNS;
                                    }
                                    else { y += 1.0; } // Avanza un poco
                                }
                                else { y += 1.0; } // Avanza un poco
                            }
                        }
                    }
                }
                catch (System.Exception ex)
                {
                     ed.WriteMessage($"\nERROR CRÍTICO durante el bucle de layout (v59): {ex.Message}");
                     layout = null; // Fallar
                }
                finally
                {
                    tr.Abort(); // Solo hemos leído datos
                }
            } // Fin del using (Transaction)

            if (layout != null)
            {
                layout.TotalTrackers = layout.LongTrackers + layout.ShortTrackers;
            }
            return layout;
        }

        // --- 'IsTrackerValid_4Corners_v59' (NUEVA FUNCIÓN v59) ---
        // Llama al algoritmo de colisión robusto v59
        private static bool IsTrackerValid_4Corners_v59(List<Point2d> netArea, List<List<Point2d>> affections, Point3d center, double length, double width)
        {
            double halfLen = length / 2.0; // Largo (Y)
            double halfWid = width / 2.0;  // Ancho (X)
            
            // Puntos 2D
            Point2d p1 = new Point2d(center.X - halfWid, center.Y - halfLen); // Abajo-Izquierda
            Point2d p2 = new Point2d(center.X + halfWid, center.Y - halfLen); // Abajo-Derecha
            Point2d p3 = new Point2d(center.X + halfWid, center.Y + halfLen); // Arriba-Derecha
            Point2d p4 = new Point2d(center.X - halfWid, center.Y + halfLen); // Arriba-Izquierda

            // Lógica de validación
            if (!IsPointValid_v59(netArea, affections, p1)) return false;
            if (!IsPointValid_v59(netArea, affections, p2)) return false;
            if (!IsPointValid_v59(netArea, affections, p3)) return false;
            if (!IsPointValid_v59(netArea, affections, p4)) return false;

            return true; // Todas las esquinas están DENTRO
        }
        
        // --- 'IsPointValid_v59' (NUEVA FUNCIÓN v59) ---
        // Llama al algoritmo de colisión robusto v59
        private static bool IsPointValid_v59(List<Point2d> netArea, List<List<Point2d>> affections, Point2d testPoint)
        {
            // Condición 1: Debe estar DENTRO del área neta
            if (netArea.Count == 0 || !IsPointInsidePoly_v59(netArea, testPoint)) { return false; }

            // Condición 2: NO debe estar dentro de NINGUNA afección
            foreach (List<Point2d> affPoly in affections)
            {
                if (affPoly.Count > 0 && IsPointInsidePoly_v59(affPoly, testPoint)) { return false; }
            }

            return true; // Pasó ambas pruebas
        }
        
        // --- 'IsPointInsidePoly_v59' (NUEVA FUNCIÓN v59) ---
        //
        // Esta es la implementación robusta del algoritmo Ray-Casting
        // que reemplaza a la versión 'v45' defectuosa.
        //
        private static bool IsPointInsidePoly_v59(List<Point2d> polygon, Point2d testPoint)
        {
            bool result = false;
            int j = polygon.Count - 1; // El último vértice
            for (int i = 0; i < polygon.Count; i++)
            {
                // Comprobar si el rayo horizontal cruza el segmento (i, j)
                if ( (polygon[i].Y < testPoint.Y && polygon[j].Y >= testPoint.Y) 
                   || (polygon[j].Y < testPoint.Y && polygon[i].Y >= testPoint.Y) )
                {
                    // Si el segmento cruza, calcular la intersección X
                    // Evitar división por cero (aunque la condición 'if' anterior ya lo previene)
                    if (polygon[j].Y - polygon[i].Y != 0)
                    {
                        // (testPoint.Y - polygon[i].Y) / (polygon[j].Y - polygon[i].Y) es el 't' (0 a 1)
                        // (polygon[j].X - polygon[i].X) es el delta X
                        // polygon[i].X es el X inicial
                        double x_intercept = polygon[i].X + (testPoint.Y - polygon[i].Y) * (polygon[j].X - polygon[i].X) / (polygon[j].Y - polygon[i].Y);
                        
                        if (x_intercept < testPoint.X)
                        {
                            // El cruce está a la izquierda del punto. Invertir el resultado.
                            result = !result;
                        }
                    }
                }
                j = i; // j se convierte en el vértice anterior para la próxima iteración
            }
            return result;
        }


        // --- 'GetTessellatedVertices_v53' (FUNCIÓN ANTIGUA v53) ---
        //
        // Mantenemos esta función de v53, ya que compilaba correctamente
        // y usaba 'GetPointAtDist' (compatible con API antigua).
        //
        private static List<Point2d> GetTessellatedVertices_v53(Curve curve)
        {
            List<Point2d> vertices = new List<Point2d>();
            if (curve == null) return vertices;

            try
            {
                const double TESSELLATION_DISTANCE = 0.5; // 0.5m de precisión
                double totalLength = curve.GetDistanceAtParameter(curve.EndParam);
                double currentDistance = 0;

                while (currentDistance < totalLength)
                {
                    Point3d pt3d = curve.GetPointAtDist(currentDistance);
                    vertices.Add(new Point2d(pt3d.X, pt3d.Y));
                    currentDistance += TESSELLATION_DISTANCE;
                }
                Point3d endPt = curve.GetPointAtDist(totalLength);
                vertices.Add(new Point2d(endPt.X, endPt.Y));
            }
            catch (System.Exception ex)
            {
                Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage($"\n¡ERROR CRÍTICO al teselar curva (v53/59)!: {ex.Message}.");
                vertices.Clear(); // Devuelve una lista vacía
            }

            if (vertices.Count > 0 && vertices[0] != vertices[vertices.Count - 1])
            {
                vertices.Add(vertices[0]);
            }

            return vertices;
        }


        // --- 'CreateTrackerPolyline_NS' (Sin cambios) ---
        private static Polyline CreateTrackerPolyline_NS(Point3d center, double length, double width, string layer)
        {
            double halfLen = length / 2.0; // Y-axis
            double halfWid = width / 2.0;  // X-axis

            Polyline rect = new Polyline();
            rect.SetDatabaseDefaults();

            if (layer != "temp") { rect.Layer = layer; }

            rect.AddVertexAt(0, new Point2d(center.X - halfWid, center.Y - halfLen), 0, 0, 0);
            rect.AddVertexAt(1, new Point2d(center.X + halfWid, center.Y - halfLen), 0, 0, 0);
            rect.AddVertexAt(2, new Point2d(center.X + halfWid, center.Y + halfLen), 0, 0, 0);
            rect.AddVertexAt(3, new Point2d(center.X - halfWid, center.Y + halfLen), 0, 0, 0);
            rect.Closed = true;

            return rect;
        }

        // --- 'DrawFinalLayout' (Sin cambios, v40) ---
        private static void DrawFinalLayout(Database db, LayoutResult winningLayout)
        {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                foreach (Polyline trackerPoly in winningLayout.TrackersToDraw)
                {
                    btr.AppendEntity(trackerPoly);
                    tr.AddNewlyCreatedDBObject(trackerPoly, true);
                }

                tr.Commit();
            }
        }
    }
}
