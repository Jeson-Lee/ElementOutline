﻿#region Namespaces
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.IFC;
using Autodesk.Revit.UI;
using ClipperLib;
#endregion

namespace ElementOutline
{
  using Polygon = List<IntPoint>;
  using Polygons = List<List<IntPoint>>;

  [Transaction( TransactionMode.ReadOnly )]
  class Cmd2dBoolean : IExternalCommand
  {
    /// <summary>
    /// Map Point2dInt coordinates to 
    /// Clipper IntPoint instances. 
    /// </summary>
    public class VertexLookup : Dictionary<Point2dInt, IntPoint>
    {
      public IntPoint GetOrAdd( XYZ p )
      {
        Point2dInt q = new Point2dInt( p );
        if( !ContainsKey( q ) )
        {
          Add( q, new IntPoint { X = q.X, Y = q.Y } );
        }
        return this[ q ];
      }
    }

    /// <summary>
    /// Add the 2D projection of the given mesh triangles
    /// to the current element outline union
    /// </summary>
    static public bool AddToUnion(
      Polygons union,
      VertexLookup vl,
      Clipper c,
      Mesh m )
    {
      int n = m.NumTriangles;

      Polygons triangles = new Polygons( n );
      Polygon triangle = new Polygon( 3 );

      for( int i = 0; i < n; ++i )
      {
        MeshTriangle mt = m.get_Triangle( i );

        triangle.Clear();
        triangle.Add( vl.GetOrAdd( mt.get_Vertex( 0 ) ) );
        triangle.Add( vl.GetOrAdd( mt.get_Vertex( 1 ) ) );
        triangle.Add( vl.GetOrAdd( mt.get_Vertex( 2 ) ) );
        triangles.Add( triangle );
      }
      return c.AddPaths( triangles, PolyType.ptSubject, true );
    }

    /// <summary>
    /// Add the 2D projection of the given face 
    /// to the current element outline union
    /// </summary>
    static public bool AddToUnion(
      Polygons union,
      VertexLookup vl,
      Clipper c,
      Face f )
    {
      IList<CurveLoop> loops = f.GetEdgesAsCurveLoops();

      Polygons faces = new Polygons( loops.Count );

      // ExporterIFCUtils class can also be used for 
      // non-IFC purposes. The SortCurveLoops method 
      // sorts curve loops (edge loops) so that the 
      // outer loops come first.

      IList<IList<CurveLoop>> sortedLoops
        = ExporterIFCUtils.SortCurveLoops( loops );

      foreach( IList<CurveLoop> loops2
        in sortedLoops )
      {
        foreach( CurveLoop loop in loops2 )
        {
          // Outer curve loops are counter-clockwise

          if( loop.IsCounterclockwise( XYZ.BasisZ ) )
          {
            foreach( Curve curve in loop )
            {
              IList<XYZ> pts = curve.Tessellate();

              IntPoint a = vl.GetOrAdd( pts[ 0 ] );

              int n = pts.Count;
              Polygon face2d = new Polygon( n );
              face2d.Add( a );

              for( int i = 1; i < n; ++i )
              {
                IntPoint b = vl.GetOrAdd( pts[ i ] );

                if( b != a )
                {
                  face2d.Add( b );
                  a = b;
                }
                faces.Add( face2d );
              }
            }
          }
        }
      }
      return c.AddPaths( faces, PolyType.ptSubject, true );
    }

    /// <summary>
    /// Return the union of all outlines projected onto
    /// the XY plane from the geometry solids and meshes
    /// </summary>
    public bool AddToUnion(
      Polygons union,
      VertexLookup vl,
      Clipper c,
      GeometryElement geoElem )
    {
      //c.AddPaths( subjects, PolyType.ptSubject, true );
      //c.AddPaths( clips, PolyType.ptClip, true );
      //solution.Clear();
      bool succeeded = c.Execute( ClipType.ctUnion, union,
        PolyFillType.pftPositive, PolyFillType.pftPositive );

      foreach( GeometryObject obj in geoElem )
      {
        // Curve
        // Edge
        // Face
        // GeometryElement
        // GeometryInstance
        // Mesh
        // Point
        // PolyLine
        // Profile
        // Solid

        // Skip objects that contribute no 2D surface

        if( obj is Curve )
        {
          continue;
        }

        Solid solid = obj as Solid;
        if( null != solid )
        {
          foreach( Face f in solid.Faces )
          {
            // Skip pretty common case: vertical planar face

            if( f is PlanarFace
              && Util.IsHorizontal( ((PlanarFace) f).FaceNormal ) )
            {
              continue;
            }
            AddToUnion( union, vl, c, f );
          }
          continue;
        }

        Mesh mesh = obj as Mesh;
        if( null != mesh )
        {
          AddToUnion( union, vl, c, mesh );
          continue;
        }

        GeometryInstance inst = obj as GeometryInstance;
        if( null != inst )
        {
          GeometryElement txGeoElem
            = inst.GetInstanceGeometry(
              inst.Transform );

          AddToUnion( union, vl, c, txGeoElem );
          continue;
        }
        Debug.Assert( false,
          "expected only solid, mesh or instance" );
      }
      return true;
    }

    /// <summary>
    /// Convert Clipper polygons to JtLoops
    /// </summary>
    JtLoops ConvertToLoops( Polygons union )
    {
      JtLoops loops = new JtLoops( union.Count );
      JtLoop loop = new JtLoop( union.First<Polygon>().Count );
      foreach( Polygon poly in union )
      {
        loop.Clear();
        foreach( IntPoint p in poly )
        {
          loop.Add( new Point2dInt( p.X, p.Y ) );
        }
        loops.Add( loop );
      }
      return loops;
    }

    public Result Execute(
      ExternalCommandData commandData,
      ref string message,
      ElementSet elements )
    {
      UIApplication uiapp = commandData.Application;
      UIDocument uidoc = uiapp.ActiveUIDocument;
      Application app = uiapp.Application;
      Document doc = uidoc.Document;

      if( null == doc )
      {
        Util.ErrorMsg( "Please run this command in a valid"
          + " Revit project document." );
        return Result.Failed;
      }

      ICollection<ElementId> ids
        = Util.GetSelectedElements( uidoc );

      if( (null == ids) || (0 == ids.Count) )
      {
        return Result.Cancelled;
      }

      // Third attempt: create the element 2D outline 
      // from element solid faces and meshes in current 
      // view by projecting them onto the XY plane and 
      // executing 2d Boolean unions on them.

      View view = doc.ActiveView;

      Options opt = new Options
      {
        View = view
      };

      JtLoops loops = new JtLoops( 1 );

      Clipper c = new Clipper();
      VertexLookup vl = new VertexLookup();
      Polygons union = new Polygons();
      Dictionary<int, JtLoops> booleanLoops
        = new Dictionary<int, JtLoops>( ids.Count );

      foreach( ElementId id in ids )
      {
        Element e = doc.GetElement( id );
        GeometryElement geo = e.get_Geometry( opt );

        c.Clear();
        vl.Clear();
        union.Clear();

        AddToUnion( union, vl, c, geo );

        loops = ConvertToLoops( union );

        booleanLoops.Add( id.IntegerValue, loops );
      }

      string filepath = Path.Combine( Util.OutputFolderPath,
         doc.Title + "_element_2d_boolean_outline.json" );

      Util.ExportLoops( filepath, doc, booleanLoops );

      return Result.Succeeded;
    }
  }
}
