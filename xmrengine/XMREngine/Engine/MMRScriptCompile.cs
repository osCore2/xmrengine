/***************************************************\
 *  COPYRIGHT 2009, Mike Rieker, Beverly, MA, USA  *
 *  All rights reserved.                           *
\***************************************************/

/**
 * @brief Main program for the script compiler.
 */

using System;
using System.IO;
using System.Reflection;
using log4net;
using OpenMetaverse;

namespace OpenSim.Region.ScriptEngine.XMREngine
{
    public class ScriptCompile
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private static uint fileno = 0;

        /**
         * @brief Compile a script to produce a ScriptObjCode object
         * @param source = 'source' contains the whole script source
         * @param descName = descriptive name
         * @param assetID = the script asset ID, unique per script source file
         * @param scriptBasePath = where to put files (a directory)
         * @param errorMessage = where to write error messages to
         * @returns object code pointer or null if compile error
         */
        public static ScriptObjCode Compile (string source, 
                                             string descName,
                                             string assetID,
                                             string scriptBasePath,
                                             TokenErrorMessage errorMessage)
        {
            string envar = null;
            string fname = Path.Combine (scriptBasePath, assetID);
            string objFileName = GetObjFileName (assetID, scriptBasePath);

            /*
             * If we already have an object file, don't bother compiling.
             */
            if (!File.Exists (objFileName)) {

                /*
                 * Maybe write script source to a file for debugging.
                 */
                envar = Environment.GetEnvironmentVariable ("MMRScriptCompileSaveSource");
                if ((envar != null) && ((envar[0] & 1) != 0)) {
                    m_log.DebugFormat("[XMREngine]: MMRScriptCopmileSaveSource: saving to {0}.lsl", fname);
                    File.WriteAllText (fname + ".lsl", source);
                }

                /*
                 * Parse source string into tokens.
                 */
                TokenBegin tokenBegin =
                            TokenBegin.Construct(errorMessage, source);
                if (tokenBegin == null)
                {
                    m_log.DebugFormat("[XMREngine]: Tokenizing error on {0}", assetID);
                    return null;
                }

                /*
                 * Create abstract syntax tree from raw tokens.
                 */
                TokenScript tokenScript = ScriptReduce.Reduce(tokenBegin);
                if (tokenScript == null)
                {
                    m_log.DebugFormat("[XMREngine]: Reducing error on {0}", assetID);
                    return null;
                }

                /*
                 * Scan abstract syntax tree to write object file.
                 */
                if (!ScriptCodeGen.CodeGen(tokenScript, assetID, objFileName))
                {
                    m_log.DebugFormat("[XMREngine]: Codegen error on {0}", assetID);
                    File.Delete (objFileName);
                    return null;
                }
            }

            /*
             * Read object file to create ScriptObjCode object.
             * Maybe also write disassembly to a file for debugging.
             */
            BinaryReader objFileReader = new BinaryReader (File.OpenRead (objFileName));

            envar = Environment.GetEnvironmentVariable ("MMRScriptCompileSaveILGen");
            StreamWriter asmFileWriter = null;
            if ((envar != null) && ((envar[0] & 1) != 0)) {
                string asmFileName = fname + ".xmrasm";
                m_log.DebugFormat("[XMREngine]: MMRScriptCopmileSaveILGen: saving to {0}", asmFileName);
                asmFileWriter = File.CreateText (asmFileName);
            }

            ScriptObjCode scriptObjCode = null;
            try {
                scriptObjCode = ScriptCodeGen.PerformGeneration (objFileReader, asmFileWriter);
            } finally {
                objFileReader.Close ();
                if (asmFileWriter != null) {
                    asmFileWriter.Flush ();
                    asmFileWriter.Close ();
                }
            }

            return scriptObjCode;
        }

        public static string GetObjFileName (string assetID,
                                             string scriptBasePath)
        {
            string fname = Path.Combine (scriptBasePath, assetID);
            return fname + ".xmrobj";
        }
    }
}
