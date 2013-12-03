//
// ES2Renderer.cs
// RhinoMobile.Display
//
// Created by dan (dan@mcneel.com) on 9/17/2013
// Copyright 2013 Robert McNeel & Associates.  All rights reserved.
// OpenNURBS, Rhinoceros, and Rhino3D are registered trademarks of Robert
// McNeel & Associates.
//
// THIS SOFTWARE IS PROVIDED "AS IS" WITHOUT EXPRESS OR IMPLIED WARRANTY.
// ALL IMPLIED WARRANTIES OF FITNESS FOR ANY PARTICULAR PURPOSE AND OF
// MERCHANTABILITY ARE HEREBY DISCLAIMED.
//
using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using OpenTK.Graphics;
using OpenTK.Graphics.ES20;

using Rhino.DocObjects;
using RhinoMobile.Model;
using RhinoMobile.Display;

#if __IOS__
using MonoTouch.UIKit;
using MonoTouch.Foundation;
using MonoTouch.OpenGLES;
#endif

#region OpenTK-1.0 API diffs
// This region handles differences between OpenTK-1.0  on MonoDroid and MonoTouch.  
// MonoDroid is behind the times and has not yet caught up with MonoTouch
// on the OpenTK-1.0 front.  Once things stabilize, this can be removed. 
// See this thread for details:
// http://forums.xamarin.com/discussion/1939/renderbuffertarget-in-opentk
#if __ANDROID__
using BufferTarget = OpenTK.Graphics.ES20.All;
using BufferUsage = OpenTK.Graphics.ES20.All;
using VertexAttribPointerType = OpenTK.Graphics.ES20.All;
using ShaderType = OpenTK.Graphics.ES20.All;
using EnableCap = OpenTK.Graphics.ES20.All;
using ProgramParameter = OpenTK.Graphics.ES20.All;
using ShaderParameter = OpenTK.Graphics.ES20.All;
using GetError = OpenTK.Graphics.ES20.All;
using Color4 = OpenTK.Graphics.ES20.All;
using DepthFunction = OpenTK.Graphics.ES20.All;
using BlendingFactorSrc = OpenTK.Graphics.ES20.All;
using BlendingFactorDest = OpenTK.Graphics.ES20.All;
using CullFaceMode = OpenTK.Graphics.ES20.All;
using FramebufferTarget = OpenTK.Graphics.ES20.All;
using RenderbufferTarget = OpenTK.Graphics.ES20.All;
#endif
#endregion

namespace RhinoMobile.Display
{
	public class ES2Renderer : IRenderer
	{
		public enum AGMColors : int
		{
			AgmRedCyan,
			AgmAmberBlue,
			AgmMagentaGreen
		}

		#region members
		RMModel m_model;
		Material m_currentMaterial;

		// shaders are created on demand and stored in a list
		List<RhGLShaderProgram> m_shaders = new List<RhGLShaderProgram>();

		// Active shader reference used to "track" the current shader object...
		RhGLShaderProgram m_activeShader;
		#endregion

		#region properties
		/// <value> Frame should be passed in by the parent view. </value>
		public System.Drawing.RectangleF Frame { get; set; }

		/// <value> The ViewportInfo that is currently being rendered. </value>
		public ViewportInfo Viewport { get; set; }

		#if __ANDROID__
		/// <value> This is the application context from Android which is necessary in order to retreive items from the bundle. </value>
		public Android.Content.Context AndroidContext { get; set; }
		#endif 
		#endregion

		#region Shaders
		/// <summary>
		/// GetShader loads a shader resource from disk
		/// </summary>
		private RhGLShaderProgram GetShader (string baseName)
		{
			for (int i = 0; i<m_shaders.Count; i++) {
				var shader = m_shaders [i];
				if (shader.Name.Equals (baseName, StringComparison.InvariantCultureIgnoreCase)) {
					SetupShader (shader, m_model, Viewport);
					return shader;
				}
			}
		
			String vertex_shader = GetResourceAsString (baseName, "vsh");
			String fragment_shader = GetResourceAsString (baseName, "fsh");
		
			var new_shader = RhGLShaderProgram.BuildProgram (baseName, vertex_shader, fragment_shader);
			if (new_shader != null)
				m_shaders.Add (new_shader);

			SetupShader (new_shader, m_model, Viewport);
			return new_shader;
		}

		/// <summary>
		/// Platform-Specific implementations
		/// <para>Android: returns the shader as a string from assets.</para>
		/// <para>iOS: returns the shader as a string from the app bundle.</para>
		/// </summary>
		private string GetResourceAsString(string name, string extension)
		{
			string resource = string.Empty;
			string contents = string.Empty;

			#if __ANDROID__
			resource = name + "." + extension;
			string shaderPath = Path.Combine ("Shaders", resource);

			var context = AndroidContext;
			System.IO.StreamReader sr = new System.IO.StreamReader (context.Assets.Open (shaderPath));
			contents = sr.ReadToEnd ();
			sr.Close();
			sr.Dispose();
			#endif

			#if __IOS__
			string shaderPath = System.IO.Path.Combine ("Shaders", name);
			resource = NSBundle.MainBundle.PathForResource(shaderPath, extension);
			contents = System.IO.File.ReadAllText (resource);
			#endif

			return contents;
		}

		/// <summary>
		/// Creates a default light and enables the active shader
		/// </summary>
		private void SetupShader (RhGLShaderProgram shader, RMModel model, ViewportInfo viewport)
		{
			 
			// Check to make sure we actually have an active shader...
			if (shader != null) {
				Rhino.Geometry.Light light = new Rhino.Geometry.Light ();

				Rhino.Geometry.Point3f lightLocation = new Rhino.Geometry.Point3f  (10, 10, 10);
				light.Location = new Rhino.Geometry.Point3d (lightLocation);
				light.Specular = System.Drawing.Color.FromArgb(255, 90, 90, 90);

				// unfortunately ON assumes right-handedness...so flip
				Rhino.Geometry.Vector3d lightDir = light.Direction;
				Rhino.Geometry.Vector3d lightDirFlippedZ = new Rhino.Geometry.Vector3d (lightDir.X, lightDir.Y, -lightDir.Z);
				light.Direction = lightDirFlippedZ;

				// Enable...
				shader.Enable ();

				// Now setup and initialize frustum and lighting...
				int near, far;
				viewport.GetScreenPort (out near, out far);
				viewport.SetScreenPort ((int)Frame.Left, (int)Frame.Right, (int)Frame.Bottom, (int)Frame.Top, near, far); 
				shader.SetupViewport (viewport);
				shader.SetupLight (light);
				light.Dispose ();
			}
		}
		#endregion

		#region Display Methods
		/// <summary>
		/// IRenderer interface method
		/// Renders a model in a viewport
		/// </summary>
		public bool RenderModel (RMModel model, Rhino.DocObjects.ViewportInfo viewport)
		{
			m_model = model;

      ClearView ();
      if ((model == null) || !model.IsReadyForRendering)
        return false;

      viewport.SetFrustumNearFar (model.BBox);

			// Disable Blending and set the depth function
			GL.DepthFunc (DepthFunction.Gequal);
			GL.Disable (EnableCap.Blend);

			// Get the shader...
			m_activeShader = GetShader ("PerPixelLighting");

			// Render calls...
			if (model != null) {
				// First render all opaque objects...
				foreach (var obj in model.DisplayObjects) {
					if (obj != null) {
						RenderObject (obj, viewport);
					}
				}

				// Second render all transparent meshes
				RenderTransparentObjects (model);
			}

			// Disable the shader
			m_activeShader.Disable ();

			return true;
		}

		/// <summary>
		/// Renders the object in a viewport
		/// </summary>
		private void RenderObject (DisplayObject obj, Rhino.DocObjects.ViewportInfo viewport)
		{
			// If the object is invisible, return.
			if (!obj.IsVisible)
				return;

			// If the layer that the object is on is turned off, return.
			if (!m_model.LayerIsVisibleAtIndex (obj.LayerIndex))
				return;

			DisplayMesh displayMesh;
			if (obj.GetType () == Type.GetType ("RhinoMobile.Display.DisplayMesh")) {
				displayMesh = (DisplayMesh)obj;
			} else if (obj.GetType () == Type.GetType ("RhinoMobile.Display.DisplayInstanceMesh")) {
				displayMesh = ((DisplayInstanceMesh)obj).Mesh;
			} else {
				displayMesh = obj.Mesh;
			}

			if (displayMesh != null) {

				SetMaterial (displayMesh.Material);

				if (displayMesh.VertexBufferHandle == Globals.UNSET_HANDLE) {

					// Generate the VertexBuffer
					uint vertex_buffer;
					GL.GenBuffers (1, out vertex_buffer);
					GL.BindBuffer (BufferTarget.ArrayBuffer, vertex_buffer);
					displayMesh.VertexBufferHandle = vertex_buffer;

					// Set the buffer data
					if (!displayMesh.HasVertexNormals && !displayMesh.HasVertexColors) { // Vertices only
						GL.BufferData (BufferTarget.ArrayBuffer, (IntPtr)(displayMesh.Vertices.Length * displayMesh.Stride), displayMesh.Vertices, BufferUsage.StaticDraw);
					} else if (displayMesh.HasVertexNormals && !displayMesh.HasVertexColors) { // VerticesNormals
						GL.BufferData (BufferTarget.ArrayBuffer, (IntPtr)(displayMesh.VerticesNormals.Length * displayMesh.Stride), displayMesh.VerticesNormals, BufferUsage.StaticDraw);
					} else if (!displayMesh.HasVertexNormals && displayMesh.HasVertexColors) { // VerticesColors
						GL.BufferData (BufferTarget.ArrayBuffer, (IntPtr)(displayMesh.VerticesColors.Length * displayMesh.Stride), displayMesh.VerticesColors, BufferUsage.StaticDraw);
					} else if (displayMesh.HasVertexNormals && displayMesh.HasVertexColors) { // VerticesNormalsColors
						GL.BufferData (BufferTarget.ArrayBuffer, (IntPtr)(displayMesh.VerticesNormalsColors.Length * displayMesh.Stride), displayMesh.VerticesNormalsColors, BufferUsage.StaticDraw);
					}
				}

				if (displayMesh.IndexBufferHandle == Globals.UNSET_HANDLE) {
					// Index VBO
					uint index_buffer;
					GL.GenBuffers (1, out index_buffer);
					GL.BindBuffer (BufferTarget.ElementArrayBuffer, index_buffer);
					GL.BufferData (BufferTarget.ElementArrayBuffer, (IntPtr)(displayMesh.Indices.Length*sizeof(int)), displayMesh.Indices, BufferUsage.StaticDraw);
					displayMesh.IndexBufferHandle = index_buffer;
				}

				// Bind Vertices
				// ORDER MATTERS...if you don't do things in this order, you will get very frusterated.
				// First, enable the VertexAttribArray for positions
				int rglVertex = m_activeShader.RglVertexIndex;
				GL.EnableVertexAttribArray (rglVertex);
				// Second, Bind the ArrayBuffer
				GL.BindBuffer (BufferTarget.ArrayBuffer, displayMesh.VertexBufferHandle);
				// Third, tell GL where to look for the data...
				GL.VertexAttribPointer (rglVertex, 3, VertexAttribPointerType.Float, false, displayMesh.Stride, IntPtr.Zero);

				// Bind Normals
				if (displayMesh.HasVertexNormals) {
					int rglNormal = m_activeShader.RglNormalIndex;
					GL.EnableVertexAttribArray (rglNormal);
					GL.BindBuffer (BufferTarget.ArrayBuffer, displayMesh.VertexBufferHandle);
					GL.VertexAttribPointer (rglNormal, 3, VertexAttribPointerType.Float, false, displayMesh.Stride, (IntPtr)(Marshal.SizeOf (typeof(Rhino.Geometry.Point3f))));
				}

				// Bind Colors
				if (displayMesh.HasVertexColors) {
					int rglColor = m_activeShader.RglColorIndex;
					GL.EnableVertexAttribArray (rglColor);
					GL.BindBuffer (BufferTarget.ArrayBuffer, displayMesh.VertexBufferHandle);
					GL.VertexAttribPointer (rglColor, 4, VertexAttribPointerType.Float, false, displayMesh.Stride, (IntPtr)(Marshal.SizeOf (typeof(Rhino.Display.Color4f))));
				}

				// Push transforms from instances onto the uniform stack
				if (obj.GetType () == Type.GetType ("RhinoMobile.Display.DisplayInstanceMesh")) {
					m_activeShader.PushModelViewMatrix ((obj as DisplayInstanceMesh).XForm);
				}

				// Bind Indices
				GL.BindBuffer (BufferTarget.ElementArrayBuffer, displayMesh.IndexBufferHandle);

				// Draw...
				#if __ANDROID__
				GL.DrawElements (All.Triangles, displayMesh.IndexBufferLength, All.UnsignedInt, IntPtr.Zero);
				#endif

				#if __IOS__
				GL.DrawElements (BeginMode.Triangles, displayMesh.IndexBufferLength, DrawElementsType.UnsignedInt, IntPtr.Zero);
				#endif
			
				// Pop transforms from instances off of the uniform stack
				if (obj.GetType () == Type.GetType ("RhinoMobile.Display.DisplayInstanceMesh")) {
					m_activeShader.PopModelViewMatrix ();
				}

				GL.DisableVertexAttribArray (rglVertex);
			}

		}

		/// <summary>
		/// Draws all transparent meshes
		/// </summary>
		private void RenderTransparentObjects (RMModel model)
		{
			// Drawing transparent meshes is a 3 pass process...
			//
			// Pass #1: With depth buffer writing OFF
			//            i. Draw all objects' backfaces
			//           ii. Draw all "open" objects' front faces.
			//
			// Pass #2: With depth buffer writing ON
			//            i. Draw all objects' front faces
			//
			// Pass #3: With depth buffer writing ON
			//            i. Draw all "open" objects' back faces
			//

			// Provided we actually have a model to render...
			if (model != null) {
				// ... render all transparent meshes...
				List<DisplayObject> objects = model.TransparentObjects;

				if (objects.Count > 0) {
					//Pass #1
					GL.DepthMask (false);
					GL.Enable (EnableCap.CullFace);

					// i. Draw all objects' backfaces
					GL.CullFace (CullFaceMode.Front);
					foreach (DisplayObject obj in objects) {

						DisplayMesh mesh;
						if (obj.GetType () == Type.GetType ("RhinoMobile.Display.DisplayMesh")) {
							mesh = (DisplayMesh)obj;
						} else if (obj.GetType () == Type.GetType ("RhinoMobile.Display.DisplayInstanceMesh")) {
							mesh = ((DisplayInstanceMesh)obj).Mesh;
						} else {
							mesh = obj.Mesh;
						}

						if (mesh != null) {
							RenderObject (obj, Viewport);
							// ii. Draw all "open" objects' front faces.
							if (!mesh.IsClosed) {
								GL.CullFace (CullFaceMode.Back);
								RenderObject (obj, Viewport);
							}
						}
					}

					// Pass #2: Draw all objects' front faces
					GL.DepthMask (true);
					GL.CullFace (CullFaceMode.Back);
					foreach (DisplayObject obj in objects) {
						RenderObject (obj, Viewport);
					}

					// Pass #3: Draw all "open" objects' back faces
					GL.CullFace (CullFaceMode.Front);
					foreach (DisplayObject obj in objects) {

						DisplayMesh mesh;
						if (obj.GetType () == Type.GetType ("RhinoMobile.Display.DisplayMesh")) {
							mesh = (DisplayMesh)obj;
						} else if (obj.GetType () == Type.GetType ("RhinoMobile.Display.DisplayInstanceMesh")) {
							mesh = ((DisplayInstanceMesh)obj).Mesh;
						} else {
							mesh = obj.Mesh;
						}

						if ((mesh != null) && (!mesh.IsClosed))
							RenderObject (obj, Viewport);
					}

					GL.Disable (EnableCap.CullFace);
				}
			}
		}

		/// <summary>
		/// IRenderer interface method
		/// Resize should be called on view change events such as device rotation.
		/// </summary>
		public bool Resize()
		{
			return true; // lie about it
		}

		/// <summary>
		/// IRenderer interface method
		/// Clears the openGL context for the next frame.
		/// </summary>
		public bool ClearView()
		{
			// Clear color and depth buffers
			GL.ClearColor (0.9f, 0.9f, 0.9f, 1f);
			GL.Clear (ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

			return true;
		}
		#endregion

		#region Materials
		/// <summary>
		/// Sets the current material if not already set to that material
		/// </summary>
		private void SetMaterial (Material material)
		{
			if (m_activeShader != null) {
				if (!material.Equals (m_currentMaterial)) {
					m_activeShader.SetupMaterial (material);
					m_currentMaterial = material;
				}
			}
		}
		#endregion

		#region Utilities
		/// <summary>
		/// DEBUG only.
		/// <para>Checks for outstanding GL Errors and logs them to console.</para>
		/// </summary>
		public static void CheckGLError () 
		{
			#if DEBUG 
			#if __IOS__
			var err = GL.GetError ();
			do {
				if (err != ErrorCode.NoError)
					Console.WriteLine ("GL Error: {0}", err.ToString ());
				err = GL.GetError ();
			} while ((err != ErrorCode.NoError));
			#endif
			#endif
		}

		/// <summary>
		/// DEBUG only.
		/// Writes supported graphics modes to the console
		/// </summary>
		public static void TestGraphicsModes ()
		{
			#if DEBUG 
			Dictionary<GraphicsMode, GraphicsMode> modes = new Dictionary<GraphicsMode, GraphicsMode>();
			Console.WriteLine("Cl (RGBA): Color format (total bits and bits per channel).");
			Console.WriteLine("Dp       : Depth buffer bits.");
			Console.WriteLine("St       : Stencil buffer bits.");
			Console.WriteLine("AA       : Sample count for anti-aliasing.");
			Console.WriteLine("Stereo   : Stereoscoping rendering supported.");
			Console.WriteLine("");
			Console.WriteLine("Cl (RGBA), Dp, St, AA, Stereo");
			Console.WriteLine("-----------------------------");
			foreach (ColorFormat color in new ColorFormat[] { 32, 24, 16, 8 })
				foreach (int depth in new int[] { 24, 16 })
					foreach (int stencil in new int[] { 8, 0 })
						foreach (int samples in new int[] { 0, 2, 4, 6, 8, 16 })
							foreach (bool stereo in new bool[] { false, true })
						{
							try
							{
								GraphicsMode mode = new GraphicsMode(color, depth, stencil, samples, color, 2, stereo);
								if (!modes.ContainsKey(mode))
									modes.Add(mode, mode);
							}
							catch
							{ }
						}

			foreach (GraphicsMode mode in modes.Keys)
				Console.WriteLine(String.Format("{0}, {1:00}, {2:00}, {3:00}, {4}", mode.ColorFormat, mode.Depth, mode.Stencil, mode.Samples, mode.Stereo));
			#endif
		}
		#endregion

	} 
}