#region Copyright (c) 2002, James W. Newkirk, Michael C. Two, Alexei A. Vorontsov, Philip A. Craig
/************************************************************************************
'
' Copyright � 2002 James W. Newkirk, Michael C. Two, Alexei A. Vorontsov
' Copyright � 2000-2002 Philip A. Craig
'
' This software is provided 'as-is', without any express or implied warranty. In no 
' event will the authors be held liable for any damages arising from the use of this 
' software.
' 
' Permission is granted to anyone to use this software for any purpose, including 
' commercial applications, and to alter it and redistribute it freely, subject to the 
' following restrictions:
'
' 1. The origin of this software must not be misrepresented; you must not claim that 
' you wrote the original software. If you use this software in a product, an 
' acknowledgment (see the following) in the product documentation is required.
'
' Portions Copyright � 2002 James W. Newkirk, Michael C. Two, Alexei A. Vorontsov 
' or Copyright � 2000-2002 Philip A. Craig
'
' 2. Altered source versions must be plainly marked as such, and must not be 
' misrepresented as being the original software.
'
' 3. This notice may not be removed or altered from any source distribution.
'
'***********************************************************************************/
#endregion

namespace NUnit.Util
{
	using System;
	using System.IO;
	using System.Threading;
	using NUnit.Core;
	using NUnit.Framework;


	/// <summary>
	/// TestLoader handles interactions between a test runner and a 
	/// client program - typically the user interface - for the 
	/// purpose of loading, unloading and running tests.
	/// 
	/// It implemements the EventListener interface which is used by 
	/// the test runner and repackages those events, along with
	/// others as individual events that clients may subscribe to
	/// in collaboration with a TestEventDispatcher helper object.
	/// 
	/// TestLoader is quite handy for use with a gui client because
	/// of the large number of events it supports. However, it has
	/// no dependencies on ui components and can be used independently.
	/// </summary>
	public class TestLoader : LongLivingMarshalByRefObject, NUnit.Core.EventListener, ITestLoader
	{
		#region Instance Variables

		/// <summary>
		/// StdOut stream for use by the TestRunner
		/// </summary>
		private TextWriter stdOutWriter;

		/// <summary>
		/// StdErr stream for use by the TestRunner
		/// </summary>
		private TextWriter stdErrWriter;

		/// <summary>
		/// Our event dispatiching helper object
		/// </summary>
		private TestEventDispatcher events;

		/// <summary>
		/// Loads and executes tests. Non-null when
		/// we have loaded a test.
		/// </summary>
		private TestDomain testDomain = null;

		/// <summary>
		/// Our current test project, if we have one.
		/// </summary>
		private NUnitProject testProject = null;

		/// <summary>
		/// The currently loaded test, returned by the testrunner
		/// </summary>
		private UITestNode loadedTest = null;

		/// <summary>
		/// The test that is running
		/// </summary>
		private UITestNode runningTest = null;

		/// <summary>
		/// Result of the last test run
		/// </summary>
		private TestResult lastResult = null;

		/// <summary>
		/// The thread that is running a test
		/// </summary>
		private Thread runningThread = null;

		/// <summary>
		/// Watcher fires when the assembly changes
		/// </summary>
		private AssemblyWatcher watcher;

		/// <summary>
		/// Assembly changed during a test and
		/// needs to be reloaded later
		/// </summary>
		private bool reloadPending = false;

		#endregion

		#region Constructor

		public TestLoader(TextWriter stdOutWriter, TextWriter stdErrWriter )
		{
			this.stdOutWriter = stdOutWriter;
			this.stdErrWriter = stdErrWriter;
			this.events = new TestEventDispatcher();
		}

		#endregion

		#region Properties

		public bool IsProjectLoaded
		{
			get { return testProject != null; }
		}

		public bool IsTestLoaded
		{
			get { return loadedTest != null; }
		}

		public bool IsTestRunning
		{
			get { return runningTest != null; }
		}

		public bool IsReloadPending
		{
			get { return reloadPending; }
		}

		public NUnitProject TestProject
		{
			get { return testProject; }
			set
			{
				if ( IsProjectLoaded )
					UnloadProject();

				testProject = value;

				events.FireProjectLoaded( TestFileName );
			}
		}

		public ITestEvents Events
		{
			get { return events; }
		}

		public string TestFileName
		{
			get { return testProject.TestFileName; }
		}

		public TestResult LastResult
		{
			get { return lastResult; }
		}

		#endregion

		#region EventListener Handlers

		/// <summary>
		/// Trigger event when each test starts
		/// </summary>
		/// <param name="testCase">TestCase that is starting</param>
		void EventListener.TestStarted(NUnit.Core.TestCase testCase)
		{
			events.FireTestStarting( testCase );
		}

		/// <summary>
		/// Trigger event when each test finishes
		/// </summary>
		/// <param name="result">Result of the case that finished</param>
		void EventListener.TestFinished(TestCaseResult result)
		{
			events.FireTestFinished( result );
		}

		/// <summary>
		/// Trigger event when each suite starts
		/// </summary>
		/// <param name="suite">Suite that is starting</param>
		void EventListener.SuiteStarted(TestSuite suite)
		{
			events.FireSuiteStarting( suite );
		}

		/// <summary>
		/// Trigger event when each suite finishes
		/// </summary>
		/// <param name="result">Result of the suite that finished</param>
		void EventListener.SuiteFinished(TestSuiteResult result)
		{
			events.FireSuiteFinished( result );
		}

		#endregion

		#region Methods for Loading and Unloading Projects
		
		public void LoadProject( string filePath )
		{
			events.FireProjectLoading( filePath );

			NUnitProject newProject = NUnitProject.MakeProject( filePath );			

			if ( IsProjectLoaded )
				UnloadProject();
			
			testProject = newProject;

			events.FireProjectLoaded( TestFileName );
		}

		public void UnloadProject()
		{
			string testFileName = TestFileName;

			events.FireProjectUnloading( testFileName );

			if ( IsTestLoaded )
				UnloadTest();

			testProject = null;

			events.FireProjectUnloaded( testFileName );
		}

		#endregion

		#region Methods for Loading and Unloading Tests

		public void LoadTest( string testFileName )
		{
			LoadProject( testFileName );
			
			if( TestProject.IsLoadable )
				LoadTest();
		}

		public void SetActiveConfig( string name )
		{
			TestProject.ActiveConfig = name;

			if( TestProject.IsLoadable )
				LoadTest();
			else
				UnloadTest();
		}

		public void LoadTest( )
		{
			try
			{
				events.FireTestLoading( TestFileName );

				testDomain = new TestDomain( stdOutWriter, stdErrWriter );		
				loadedTest = TestProject.LoadTest( testDomain );
			
				lastResult = null;
				reloadPending = false;
			
				// TODO: Figure out how to handle relative paths in tests
				SetWorkingDirectory( TestProject.IsWrapper
					? TestFileName
					: testProject.ActiveAssemblies[0] );

				events.FireTestLoaded( TestFileName, this.loadedTest );

				if ( UserSettings.Options.ReloadOnChange )
					InstallWatcher( );
			}
			catch( Exception exception )
			{
				events.FireTestLoadFailed( TestFileName, exception );
			}
		}

		/// <summary>
		/// Unload the current test suite and fire the Unloaded event
		/// </summary>
		public void UnloadTest( )
		{
			if( IsTestLoaded )
			{
				// Hold the name for notifications after unload
				string fileName = TestFileName;

				try
				{
					events.FireTestUnloading( TestFileName, this.loadedTest );

					RemoveWatcher();

					testDomain.Unload();

					testDomain = null;
					//testFileName = null;
					//testProject = null;
					loadedTest = null;
					lastResult = null;
					reloadPending = false;

					events.FireTestUnloaded( fileName, this.loadedTest );
				}
				catch( Exception exception )
				{
					events.FireTestUnloadFailed( fileName, exception );
				}
			}
		}

		/// <summary>
		/// Reload the current test on command
		/// </summary>
		public void ReloadTest()
		{
			OnTestChanged( TestFileName );
		}

		/// <summary>
		/// Handle watcher event that signals when the loaded assembly
		/// file has changed. Make sure it's a real change before
		/// firing the SuiteChangedEvent. Since this all happens
		/// asynchronously, we use an event to let ui components
		/// know that the failure happened.
		/// </summary>
		/// <param name="assemblyFileName">Assembly file that changed</param>
		public void OnTestChanged( string testFileName )
		{
			if ( IsTestRunning )
				reloadPending = true;
			else 
				try
				{
					events.FireTestReloading( testFileName, this.loadedTest );

					// Don't unload the old domain till after the event
					// handlers get a chance to compare the trees.
					TestDomain newDomain = new TestDomain(stdOutWriter, stdErrWriter);
					UITestNode newTest = newDomain.Load( testFileName );

					bool notifyClient = !UIHelper.CompareTree( this.loadedTest, newTest );

					testDomain.Unload();

					testDomain = newDomain;
					loadedTest = newTest;
					reloadPending = false;

					if ( notifyClient )
						events.FireTestReloaded( testFileName, newTest );
				
				}
				catch( Exception exception )
				{
					events.FireTestReloadFailed( testFileName, exception );
				}
		}

		#endregion

		#region Methods for Running Tests

		/// <summary>
		/// Run a testcase or testsuite from the currrent tree
		/// firing the RunStarting and RunFinished events.
		/// Silently ignore the call if a test is running
		/// to allow for latency in the UI.
		/// </summary>
		/// <param name="test">Test to be run</param>
		public void RunTestSuite( UITestNode testInfo )
		{
			if ( !IsTestRunning )
			{
				if ( IsReloadPending || UserSettings.Options.ReloadOnRun )
					ReloadTest();

				runningTest = testInfo;
				runningThread = new Thread( new ThreadStart( this.TestRunThreadProc ) );
				runningThread.Start();
			}
		}

		/// <summary>
		/// The thread proc for our actual test run
		/// </summary>
		private void TestRunThreadProc()
		{
			events.FireRunStarting( runningTest );

			try
			{
				testDomain.TestName = runningTest.FullName;
				lastResult = testDomain.Run(this );
				
				events.FireRunFinished( lastResult );
			}
			catch( Exception exception )
			{
				events.FireRunFinished( exception );
			}
			finally
			{
				runningTest = null;
				runningThread = null;
			}
		}

		/// <summary>
		/// Cancel the currently running test.
		/// Fail silently if there is none to
		/// allow for latency in the UI.
		/// </summary>
		public void CancelTestRun()
		{
			if ( IsTestRunning )
			{
				runningThread.Abort();
				runningThread.Join();
			}
		}

		#endregion

		#region Helper Methods

		private static void SetWorkingDirectory(string testFileName)
		{
			FileInfo info = new FileInfo(testFileName);
			Directory.SetCurrentDirectory(info.DirectoryName);
		}
		
		/// <summary>
		/// Install our watcher object so as to get notifications
		/// about changes to a test.
		/// </summary>
		/// <param name="assemblyFileName">Full path of the assembly to watch</param>
		private void InstallWatcher()
		{
			if(watcher!=null) watcher.Stop();

			watcher = new AssemblyWatcher( 1000, TestProject.ActiveAssemblies );
			watcher.AssemblyChangedEvent += new AssemblyWatcher.AssemblyChangedHandler( OnTestChanged );
			watcher.Start();
		}

		/// <summary>
		/// Stop and remove our current watcher object.
		/// </summary>
		private void RemoveWatcher()
		{
			if ( watcher != null )
			{
				watcher.Stop();
				watcher = null;
			}
		}

		#endregion
	}
}
