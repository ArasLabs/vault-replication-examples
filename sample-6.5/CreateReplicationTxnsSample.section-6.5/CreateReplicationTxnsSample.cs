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
  /// This is a sample code that demos how to programmatically create replication txns on Innovator server.
  /// 
  /// The sample first gets a list of files from the Innovator server and then sends a request that creates
  /// a replication transaction for the file (providing that the correspondign replication rule exists - see
  /// pre-requisites for the sample below). Note that the request for creating a replication transaction 
  /// could be send from either a client (what's done here) or from an Innovator server method.
  /// 
  /// The sample assumes that the following pre-requisites exist in the Innovator to
  /// which the sample is connecting:
  /// 
  ///   1. There are 2 vaults in Innovator: 
  ///        * Default (comes with the standard out-of-the-box Innovator)
  ///        * VaultA
  ///   2. There are items of type 'File' in Innovator that filename starts with 'file'. These files can
  ///      either be created manually or programmatically (see, for instance, how it's done in the 
  ///      'ProcessReplicationQueueSample').
  ///   3. Files with names 'file*' were created by a user who has default vault set to "Default"; so they
  ///      reside in the "Default" vault.
  ///   4. "Default" vault has the following replicaiton rule:
  ///        * Initiator Type: onEvent
  ///        * Filter Method: {none}
  ///        * Replication Mode: {any mode}
  ///        * Replication Type: {any type}
  ///        * Replication Time: {none}
  ///        * Timeout: {none}
  ///        * Is Active: true
  ///        * Label: {whatever label}
  ///        * File Types (tab): {empty}
  ///        * Target Vaults (tab): VaultA
  /// 
  /// Note that depending on the rule's replication mode created txns could be processed either by
  /// a code similar to samples from sections 6.1 and 6.2 of the file replication user documentation
  /// or by scheduling a queue processing thread inside the Innovator server (see documentation
  /// for more details).
  /// </summary>
  class CreateReplicationTxnsSample
  {
    // Data members
    private Innovator inn;
    private Item dvault;
    private Item files;

    static void Main(string[] args)
    {
      if (args.Length < 2)
      {
        Console.WriteLine("\n\nUsage:  CreateReplicationTxnsSample.exe {url} {db} [{user name} {password}]\n");
        return;
      }

      CreateReplicationTxnsSample p = null;
      try
      {
        p = new CreateReplicationTxnsSample(args);

        p.getDefaultVault();
        p.getFiles();
        p.showTxnsSummary();
        p.createReplicationTxns();
        p.showTxnsSummary();
      }
      catch (Exception ex)
      {
        Console.WriteLine("ERROR: " + ex.Message);
      }
    }

    CreateReplicationTxnsSample(string[] args)
    {
      String url = args[0];
      String db = args[1];
      String user = "ak";
      String pwd = "ak";

      if (args.Length > 2)
        user = args[2];
      if (args.Length > 3)
        pwd = args[3];

      login(url, db, user, pwd);
    }

    void getDefaultVault()
    {
      string aml = @"<Item type='Vault' action='get' select='id'><name>Default</name></Item>";
      Item request = this.inn.newItem();
      request.loadAML(aml);
      this.dvault = request.apply();
      if( this.dvault.isError() )
        throw new Exception( this.dvault.getErrorString() );
    }

    void getFiles()
    {
      // Get files that name starts with 'file' and that reside in the 'Default' vault.
      string aml = String.Format("<Item type='File' action='get' levels='1' config_path='Located' select='id, filename'>" + 
        "<filename condition='like'>file*</filename>" + 
        "<Relationships>" + 
        "<Item type='Located' action='get'>" +
        "<related_id>{0}</related_id>" +
        "</Item>" +
        "</Relationships>" +
        "</Item>", this.dvault.getID());
      Item request = this.inn.newItem();
      request.loadAML(aml);
      this.files = request.apply();
      if (this.files.isError())
        throw new Exception(this.files.getErrorString());
    }

    void showTxnsSummary()
    {
      checkResult("ReplicationTxn");
    }

    void createReplicationTxns()
    {
      string aml_template = "<Item type='File' action='replicate' id='{0}'>" +
        "<Relationships>" +
        "<Item type='Located' action='get'>" +
        "<related_id>" +
        "<Item type='Vault' id='{1}'/>" +
        "</related_id>" +
        "</Item>" +
        "</Relationships>" +
        "</Item>";

      string dvid = this.dvault.getID();
      int fcount = this.files.getItemCount();
      for (int i = 0; i < fcount; i++)
      {
        Item f = this.files.getItemByIndex(i);
        string aml = String.Format(aml_template, f.getID(), dvid);
        Item request = this.inn.newItem();
        request.loadAML(aml);
        Item response = request.apply();
        if (response.isError())
        {
          Console.WriteLine("Failed to send request for replication of file '{0}' - {1}", f.getProperty("filename"), response.getErrorString());
        }
      }
    }

    void login(string url, string db, string uname, string pwd)
    {
      HttpServerConnection conn = Aras.IOM.IomFactory.CreateHttpServerConnection(url, db, uname, pwd);

      Console.WriteLine("Trying to login as '{0}'...", uname);
      Item log_result = conn.Login();
      if (log_result.isError())
      {
        throw new Exception("Failed login to Innovator");
      }

      this.inn = new Innovator(conn);

      Console.WriteLine("Logged in as user '{0}'", uname);
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
  }
}
