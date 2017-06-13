using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Xml;
using System.IO;

using Aras.IOM;

namespace Aras.Samples.Replication
{
  /// <summary>
  /// This is a sample code that demos how to process replication queue on Innovator server.
  /// 
  /// First, the sample creates files and submits them to Innovator (this creates replication txns).
  /// Second, the sample process the replication queue untill it's empty.
  /// Third, all created files are removed from disk and Innovator returning to the state that was
  /// prior to the test start.
  /// 
  /// The sample assumes that the following pre-requisites exist in the Innovator to
  /// which the sample is connecting:
  /// 
  ///   1. There are following users in Innovator:
  ///        * admin - standard Innovator's "admin"; password "innovator" is assumed.
  ///        * 2 users: first - a replication user with minimal priviledges who belongs to "World" identity 
  ///                   only; second - any user who has priviledges to create items of type 'File'. Specific 
  ///                   user name \ password for both users must be specified in the configuration file (see below).
  ///   2. There are 2 vaults in Innovator: 
  ///        * Default (comes with the standard out-of-the-box Innovator)
  ///        * VaultA
  ///   3. The second user has his default vault set to "Default"
  ///   4. "Default" vault has the following replicaiton rule:
  ///        * Initiator Type: onChange
  ///        * Filter Method: <none>
  ///        * Replication Mode: Immediate
  ///        * Replication Type: Copy
  ///        * Replication Time: <none>
  ///        * Timeout: <none>
  ///        * Is Active: true
  ///        * Label: First Rule
  ///        * File Types (tab): <empty>
  ///        * Target Vaults (tab): VaultA
  /// 
  /// It's also assumed that there is a configuration file that has the following content (do NOT include 'CDATA' 
  /// open\close tag in the real file):
  /// 
  /// <![CDATA[
  /// <config>
  ///   <innovator url='...' db='...' />
  ///   <first_user name='...' password='...' />
  ///   <second_user name='...' password='...' />
  ///   <interval sec='...' />
  ///   <temp_dir path='...' />
  /// </config>
  /// ]]>
  /// 
  /// If 'temp_dir' is not specified the default "c:\temp\replication" is used. If 'interval' is not specified
  /// the default 10 (sec) is assumed. The rest of parameters are required.
  /// </summary>
  class ProcessReplicationQueueSample
  {
    // Constants. NOTE: probably also could be moved to the configuration file.
    private const int FILE_AMOUNT = 100;
    private const int WAIT_PERIOD = 20;
    private const int MAX_BATCH = 10;
    private const int MAX_PENDING = 15;

    // Data members
    private string url;
    private string db;
    private string uname1;
    private string upwd1;
    private string uname2;
    private string upwd2;
    private string tdir;
    private int interval;

    private HttpServerConnection conn;
    private Innovator inn;

    static void Main(string[] args)
    {
      ProcessReplicationQueueSample p = null;
      try
      {
        p = new ProcessReplicationQueueSample((args.Length > 0 ? args[0] : null));

        // Create physical files and submit them to Innovator.
        // This must create replication txns providing that there is 
        // required replication rule on the default vault of the second user.
        p.loginAs(p.uname2, p.upwd2);
        p.createFiles();
        p.conn.Logout();

        // Process replication queue
        p.loginAs(p.uname1, p.upwd1);
        p.processReplicationQueue();
      }
      catch (Exception ex)
      {
        Console.WriteLine("ERROR: " + ex.Message);
      }
      finally
      {
        // Cleanup physical file and all items created in Innovator.
        if (p != null && p.conn != null)
        {
          p.conn.Logout();

          Console.WriteLine("\n*** Cleaning artifacts created by the test ...");

          // First remove replication txn and logs.
          // NOTE: Need to login as admin in order to remove created replication txn records
          p.loginAs("admin", "innovator");

          Console.WriteLine("Delete 'ReplicationTxn' items ...");
          Item req = p.inn.newItem("ReplicationTxn", "delete");
          req.setAttribute("where", "id like '%'");
          Item response = req.apply();
          if (response.isError())
          {
            Console.WriteLine("Failed to delete 'ReplicationTxn' items. Please delete them manually before starting the test again");
          }

          Console.WriteLine("Delete 'ReplicationTxnLog' items ...");
          req = p.inn.newItem("ReplicationTxnLog", "delete");
          req.setAttribute("where", "id like '%'");
          response = req.apply();
          if (response.isError())
          {
            Console.WriteLine("Failed to delete 'ReplicationTxnLog' items. Please delete them manually before starting the test again");
          }

          // Finally remove physical files
          p.cleanFiles();
          p.conn.Logout();
        }

        Console.WriteLine("\n*** Finished cleaning. Exit the sample ***");
      }
    }

    ProcessReplicationQueueSample(string config_path)
    {
      if (String.IsNullOrEmpty(config_path))
        config_path = "config.xml";

      try
      {
        XmlDocument d = new XmlDocument();
        d.Load(config_path);

        XmlElement innNode = (XmlElement)d.SelectSingleNode("//innovator");
        this.url = innNode.GetAttribute("url");
        this.db = innNode.GetAttribute("db");

        XmlElement u1Node = (XmlElement)d.SelectSingleNode("//first_user");
        this.uname1 = u1Node.GetAttribute("name");
        this.upwd1 = u1Node.GetAttribute("password");

        XmlElement u2Node = (XmlElement)d.SelectSingleNode("//second_user");
        this.uname2 = u2Node.GetAttribute("name");
        this.upwd2 = u2Node.GetAttribute("password");

        XmlElement tdNode = (XmlElement)d.SelectSingleNode("//temp_dir");
        this.tdir = (tdNode == null || String.IsNullOrEmpty(tdNode.GetAttribute("path")) ? @"c:/temp/replication" : tdNode.GetAttribute("path"));

        this.interval = 10;
        XmlElement iNode = (XmlElement)d.SelectSingleNode("//interval");
        try
        {
          if( iNode != null && !String.IsNullOrEmpty(iNode.GetAttribute("sec")) )
          {
            this.interval = Int32.Parse(iNode.GetAttribute("sec"));
          }
        }
        catch (Exception)
        { }
      }
      catch(Exception exc)
      {
        throw new Exception(String.Format("Failed to open or parse the configuration file '{0}' (original error - {1})", config_path, exc.Message));
      }
    }

    void processReplicationQueue()
    {
      Console.Write("\nStart queue processing [y]? >");
      string r = Console.ReadLine();
      if (String.IsNullOrEmpty(r))
        r = "y";
      if (r.ToLower().Trim() != "y")
        return;

      while (true)
      {
        int txns_left = executeSingleProcessingCycle();
        if (txns_left > 0)
        {
          Console.WriteLine("\n\t{0} transactions left to process. Continue ...", txns_left);
          if (this.interval > 0)
            System.Threading.Thread.Sleep(this.interval * 1000);
        }
        else
        {
          // Show user the summary of replication txns and logs.
          // Prompt the user if he\she would like to continue the queue processing.
          checkReplicationResult();
          Console.Write("\nContinue queue processing [n]? >");
          r = Console.ReadLine();
          if (String.IsNullOrEmpty(r))
            r = "n";
          if (r.ToLower().Trim() != "y")
          {
            break;
          }
        }
      }
    }

    int executeSingleProcessingCycle()
    {
      Console.WriteLine("\n*** Executing replication queue cycle");
      XmlDocument inDom = new XmlDocument();
      // Note that 'max_batch' \ 'max_pending' could be changed based on server capacity.
      inDom.LoadXml(String.Format("<Item max_batch='{0}' max_pending='{1}'/>",MAX_BATCH, MAX_PENDING));
      XmlDocument outDom = new XmlDocument();
      outDom.LoadXml("<empty/>");
      this.conn.CallAction("ProcessReplicationQueue", inDom, outDom);
      Item prq_result = this.inn.newItem();
      prq_result.loadAML(outDom.OuterXml);
      if (prq_result.isError())
        throw new Exception(prq_result.getErrorString());
      else
      {
        XmlElement itemNode = (XmlElement)outDom.SelectSingleNode("//Item");
        if (itemNode == null)
        {
          Console.WriteLine("All transactions processed");
          return 0;
        }
        else
        {
          int np = Int32.Parse(itemNode.GetAttribute("need_processing"));
          int lbo = Int32.Parse(itemNode.GetAttribute("locked_by_others"));
          Console.WriteLine("\n- Processed: {0}\n- Remains: {1}\n- Locked by others: {2}", itemNode.GetAttribute("processed"), np, lbo);
          return np - lbo;
        }
      }
    }

    void loginAs(string uname, string pwd)
    {
      this.conn = Aras.IOM.IomFactory.CreateHttpServerConnection(this.url, this.db, uname, pwd);

      Console.WriteLine("Trying to login as '{0}'...", uname);
      Item log_result = this.conn.Login();
      if (log_result.isError())
      {
        throw new Exception("Failed login to Innovator");
      }

      this.inn = new Innovator(this.conn);

      Console.WriteLine("Logged in as user '{0}'", uname);
    }

    void createFiles()
    {
      Console.WriteLine("\n*** Creating files in dir " + this.tdir);

      if (!Directory.Exists(this.tdir))
        Directory.CreateDirectory(this.tdir);

      DirectoryInfo di = new DirectoryInfo(this.tdir);
      FileInfo[] files = di.GetFiles();
      foreach (FileInfo f in files)
      {
        f.Delete();
      }

      for (int i = 0; i < FILE_AMOUNT; i++)
      {
        Console.Write(".");
        string fname = string.Format("file{0:D4}_replication_sample.txt", (i+1));
        string fpath = Path.Combine(this.tdir, fname);
        using (StreamWriter sw = new StreamWriter(fpath))
        {
          sw.WriteLine("this is file #: " + i);
        }

        Item fitem = this.inn.newItem("File", "add");
        fitem.setProperty("filename", fname);
        fitem.attachPhysicalFile(fpath);
        Item result = fitem.apply();
        if (result.isError())
          throw new Exception(result.getErrorString());
      }

      Console.WriteLine("\n*** Done creating files in dir " + this.tdir);
    }

    int checkReplicationResult()
    {
      checkResult("ReplicationTxn");
      return checkResult("ReplicationTxnLog");
    }

    int checkResult(string it)
    {
      int rnum = 0;

      Item req = this.inn.newItem(it, "get");
      Item result = req.apply();
      if (result.isError())
      {
        if (result.getErrorCode() == "0")
          Console.WriteLine(string.Format("No items of type '{0}' found", it));
        else
          throw new Exception(result.getErrorString());
      }
      else
      {
        int ct = 0, ft = 0, dt = 0, pt = 0, nt = 0;
        int tcount = result.getItemCount();
        for (int i = 0; i < tcount; i++)
        {
          string status = result.getItemByIndex(i).getProperty("replication_status");
          if (status == "Completed")
            ct++;
          else if (status == "NotStarted")
            nt++;
          else if (status == "Pending")
            pt++;
          else if (status == "Discarded")
            dt++;
          else
            ft++;
        }
        rnum = ct;

        Console.WriteLine(string.Format("\n>>> {5}\nNot Started: {0}\nCompleted: {1}\nPending: {2}\nDiscarded: {3}\nFailed: {4}", nt, ct, pt, dt, ft, it));
      }

      return rnum;
    }

    void cleanFiles()
    {
      Console.WriteLine("\n*** Removing files from " + this.tdir);
      DirectoryInfo di = new DirectoryInfo(this.tdir);
      FileInfo[] files = di.GetFiles();
      foreach (FileInfo f in files)
      {
        Console.Write(".");
        Item req = this.inn.newItem("File", "get");
        req.setProperty("filename", f.Name);
        Item response = req.apply();
        if (!response.isError())
        {
          if (response.getLockStatus() != 0)
          {
            response.unlockItem();
          }
          f.Delete();

          req = this.inn.newItem("File", "delete");
          req.setID(response.getID());
          response = req.apply();
        }
        else
        {
          Console.WriteLine("Error getting the file '{0}': {1}", f.Name, response.getErrorString());
        }
      }
      Console.WriteLine("\n*** Done removing files from " + this.tdir);
    }
  }
}
