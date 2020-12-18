using System;
using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using JPMorrow.Revit.Documents;
using System.Diagnostics;
using System.Collections.Generic;
using JPMorrow.Revit.Tools;
using JPMorrow.Revit.Custom.View;
using JPMorrow.BICategories;
using System.Linq;
using JPMorrow.Tools.Diagnostics;
using MoreLinq;
using JPMorrow.Revit.Text;
using System.IO;
using JPMorrow.Revit.Loader;
using Autodesk.Revit.DB.Structure;
using JPMorrow.Revit.Worksets;

namespace MainApp
{
	/// <summary>
	/// Main Execution
	/// </summary>
	[Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
	[Autodesk.Revit.DB.Macros.AddInId("16824613-93A7-4981-B2CF-27AB6E52280A")]
    public partial class ThisApplication : IExternalCommand
    {

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
			string[] dataDirectories = new string[] { "families" };

			//set revit model info
			bool debugApp = false;
			ModelInfo revit_info = ModelInfo.StoreDocuments(commandData, dataDirectories, debugApp);
			IntPtr main_rvt_wind = Process.GetCurrentProcess().MainWindowHandle;

			var fixture_coll = new FilteredElementCollector(revit_info.DOC, revit_info.DOC.ActiveView.Id);
			var fixtures = fixture_coll.OfCategory(BuiltInCategory.OST_LightingFixtures).ToElements();
			var failed_fixtures = new List<ElementId>();

			var LightingFixtureView = ViewGen.CreateView(revit_info, "LightingFixtureElevationGenerated", BICategoryCollection.LightingFixtureView);

			string fam_path = ModelInfo.GetDataDirectory("families",  true);
			FamilyLoader.LoadFamilies(revit_info, fam_path, "Hanger_Anchor_Pt_Generic_Base_Family.rfa");

			WorksetManager.CreateWorkset(revit_info.DOC, "Debug Points");
			var ws_id = WorksetManager.GetWorksetId(revit_info.DOC, "Debug Points");

			FamilySymbol debug_sym = null;

			if(debugApp) {
				FilteredElementCollector el_coll = new FilteredElementCollector(revit_info.DOC);
				debug_sym = el_coll
					.OfClass(typeof(FamilySymbol)).Where(x =>
					(x as FamilySymbol).FamilyName.Equals("Hanger_Anchor_Pt_Generic_Base_Family"))
					.First() as FamilySymbol;

				using var activate = new Transaction(revit_info.DOC, "activating symbol");
				activate.Start();
				debug_sym.Activate();
				activate.Commit();
				el_coll = new FilteredElementCollector(revit_info.DOC);
			}

			var lines = new List<(Element Fixture, Line[] Lines)>();

			if(!fixtures.Any()) {
				debugger.show(err:"No Fixtures are visible in the current view: " + revit_info.DOC.ActiveView.Name);
				return Result.Succeeded;
			}

			// var ftest = revit_info.DOC.GetElement(revit_info.SEL.GetElementIds().First());

			// GET TOP FACE OF FIXTURE
			foreach(var fixture in fixtures) {

				Options opt = new Options();
				opt.View = LightingFixtureView;
				opt.ComputeReferences = true;
				var geo_el = fixture.get_Geometry(opt);
				Face final_face = null;

				foreach(var geo_obj in geo_el) {
					var geo_inst = geo_obj as GeometryInstance;
					if(geo_inst == null) continue;


					foreach(var obj in geo_inst.GetInstanceGeometry()) {
						var solid = obj as Solid;
						if(solid == null || solid.Edges.Size == 0 || solid.Faces.Size == 0) continue;

						var gstyle = revit_info.DOC.GetElement(solid.GraphicsStyleId) as GraphicsStyle;
						if(gstyle != null && gstyle.Name.Contains("Light Source")) continue;

						List<Face> faces = new List<Face>();

						foreach(Face f in solid.Faces) {
							if(f == null) continue;
							faces.Add(f);
						}

						if(!faces.Any()) continue;

						var ordered_faces = faces.OrderByDescending(x => {
							var pts = new List<XYZ>();

							foreach(CurveLoop loop in x.GetEdgesAsCurveLoops()) {
								if(loop == null) continue;
								foreach(Curve c in loop) {
									if(c == null) continue;

									var ll = c as Line;
									if(ll == null) continue;
									var pt = RGeo.DerivePointBetween(ll, ll.Length / 2);
									pts.Add(pt);
								}
							}

							var ret = pts.Any() ? pts.Select(x => x.Z).Average() : -9999;
							return ret;
						});

						if(ordered_faces == null || ordered_faces.Count() == 0) continue;
						final_face = ordered_faces.First();
					}
				}

				if(final_face == null) {
					failed_fixtures.Add(fixture.Id);
					continue;
				}

				List<Line> temp_lines = new List<Line>();
				foreach(CurveLoop loop in final_face.GetEdgesAsCurveLoops()) {
					foreach(Curve c in loop) {
						var l = c as Line;
						if(l == null) continue;
						var ll = Line.CreateBound(final_face.Project(l.GetEndPoint(0)).XYZPoint, final_face.Project(l.GetEndPoint(1)).XYZPoint);
						temp_lines.Add(ll);
					}
				}

				if(debugApp) {
					using var debug = new Transaction(revit_info.DOC, "debug symbols");
					debug.Start();

					List<ElementId> sel = new List<ElementId>();
					foreach(var pt in temp_lines.Select(x => RGeo.DerivePointBetween(x, x.Length / 2))) {
						var fa = MakeDebugPoint(revit_info, pt, debug_sym, null, ws_id);
						sel.Add(fa.Id);
					}

					debug.Commit();
					revit_info.SEL.SetElementIds(sel);
				}

				lines.Add((fixture, temp_lines.ToArray()));
			}

			// TAG FIXTURES
			using TransactionGroup tgrp = new TransactionGroup(revit_info.DOC, "Getting light fixture elevations");
			tgrp.Start();
			using Transaction tx = new Transaction(revit_info.DOC, "light fixture elevations");
			tx.Start();

			UnitFormatUtils.TryParse(revit_info.DOC.GetUnits(), UnitType.UT_Length, "2'", out double min_len);

			foreach (var pack in lines) {
				List<double> ray_measurments = new List<double>();

				foreach(var line in pack.Lines) {
					var pt = RGeo.DerivePointBetween(line, line.Length / 2);
					var ray = RevitRaycast.Cast(revit_info, LightingFixtureView, BICategoryCollection.LightingFixtureClash.ToList(), pt, RGeo.PrimitiveDirection.Up);

					if(ray.collisions.Any()) {
						var cols = ray.collisions.OrderBy(x => x.distance).ToList();
						foreach(var coll in cols) {
							if(coll.distance <= min_len) continue;
							ray_measurments.Add(coll.distance);
							break;
						}
					}
				}

				if(!ray_measurments.Any()) {
					failed_fixtures.Add(pack.Fixture.Id);
					continue;
				}

				try {
					UnitFormatUtils.TryParse(revit_info.DOC.GetUnits(), UnitType.UT_Length, "1\"", out double tolerance);

					pack.Fixture.LookupParameter("Height To Structure Min").Set("");
					pack.Fixture.LookupParameter("Height To Structure Max").Set("");

					if(ray_measurments.Count >= 2) {
						var max = ray_measurments.MaxBy(x => x).First();
						var min = ray_measurments.MinBy(x => x).First();


						var min_str = UnitFormatUtils.Format(revit_info.DOC.GetUnits(), UnitType.UT_Length, min, false, false, CustomFormatValue.FeetAndInches);
						var max_str = UnitFormatUtils.Format(revit_info.DOC.GetUnits(), UnitType.UT_Length, max, false, false, CustomFormatValue.FeetAndInches);

						if(max > min + tolerance) {
							pack.Fixture.LookupParameter("Height To Structure Min").Set(min_str);
							pack.Fixture.LookupParameter("Height To Structure Max").Set(max_str);
						}
						else {
							pack.Fixture.LookupParameter("Height To Structure Min").Set(max_str);
						}
					}
					else if(ray_measurments.Count == 1) {
						var min_str = UnitFormatUtils.Format(revit_info.DOC.GetUnits(), UnitType.UT_Length, ray_measurments.First(), false, false, CustomFormatValue.FeetAndInches);
						pack.Fixture.LookupParameter("Height To Structure #1").Set(min_str);
					}
					else {
						// failed
						failed_fixtures.Add(pack.Fixture.Id);
					}
				}
				catch {
					failed_fixtures.Add(pack.Fixture.Id);
				}

			}

			foreach(var id in failed_fixtures) {
				var fixture = revit_info.DOC.GetElement(id);
				fixture.LookupParameter("Height To Structure Min").Set("");
				fixture.LookupParameter("Height To Structure Max").Set("");
			}
			tx.Commit();
			tgrp.Assimilate();

			if(failed_fixtures.Any()) {
				debugger.show(err: failed_fixtures.Count() + " lighting fixtures failed to find any structure to derive thier elevation on." +
				" Please check to make sure thier is structure above the fixtures. They will be selected for you when you dismiss this prompt.");
				revit_info.SEL.SetElementIds(failed_fixtures);
			}

			return Result.Succeeded;
        }

		private static FamilyInstance MakeDebugPoint(
			ModelInfo info, XYZ pt, FamilySymbol sym,
			Level level, WorksetId workset_id) {

			var ha = info.DOC.Create.NewFamilyInstance(pt, sym, level, StructuralType.NonStructural);
			Parameter workset_param = ha.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM );
			workset_param.Set(workset_id.IntegerValue);
			return ha;
		}

		public static Level GetLevel(ModelInfo info, ElementId id)
		{
			var el = info.DOC.GetElement(id);
			string level_str = el.get_Parameter(BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM).AsValueString();
			var levels = new FilteredElementCollector(info.DOC).OfClass(typeof(Level)).Where(x => x.Name == level_str);
			return levels.First() as Level;
		}
    }
}