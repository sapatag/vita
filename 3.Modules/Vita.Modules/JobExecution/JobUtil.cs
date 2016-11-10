﻿using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

using Vita.Common;
using Vita.Entities;

namespace Vita.Modules.JobExecution {


  internal static class JobUtil {
    public static readonly string ArgsDelimiter = Environment.NewLine + new string('"', 4) + Environment.NewLine;

    public static IJob NewJob(this IEntitySession session, JobDefinition job, JsonSerializer serializer) {
      ValidateJobMethod(session.Context.App, job); 
      var ent = session.NewEntity<IJob>();
      ent.Code = job.Code;
      ent.ThreadType = job.ThreadType;
      ent.Id = job.Id;
      ent.Flags = job.Flags;
      var startInfo = job.StartInfo;
      ent.TargetType = startInfo.DeclaringType.Namespace + "." + startInfo.DeclaringType.Name;
      ent.TargetMethod = startInfo.Method.Name;
      ent.TargetParameterCount = startInfo.Arguments.Length;
      ent.Arguments = SerializeArguments(startInfo.Arguments, serializer);
      var rp = job.RetryPolicy;
      ent.RetryIntervalSec = rp.IntervalSeconds;
      ent.RetryCount = rp.RetryCount;
      ent.RetryPauseMinutes = rp.PauseMinutes;
      ent.RetryRoundsCount = rp.RoundsCount;
      if(job.ParentJob != null) {
        Util.Check(!ent.Flags.IsSet(JobFlags.StartOnSave),
              "Invalid job definition: the flag StartOnSave may not be set on a job with a parent job. Job code: {0}", job.Code);
        ent.ParentJob = session.GetEntity<IJob>(ent.ParentJob.Id);
        Util.Check(ent.ParentJob != null, "Parent job not found: {0}.", job.ParentJob.Id);
        ent.ParentJob.Flags |= JobFlags.HasChildJobs; 
      }
      return ent;
    }

    internal static void ValidateJobMethod(EntityApp app, JobDefinition job) {
      // We already check return type (Task or Void) in JobDefinition constructor
      // We need to check for instance methods that they are defined on global objects - that can be 'found' easily when it's time to invoke the method.
      var method = job.StartInfo.Method; 
      if (!method.IsStatic) {
        // it is instance method; check that it is defined on one of global objects - module, service, or custom global object
        var obj = app.GetGlobalObject(method.DeclaringType);
        Util.Check(obj != null, "Job/task method {0}.{1} is an instance method, but is defined on object that is not EntityModule, service or any registered global object.", 
          method.DeclaringType, method.Name);
      }
    }

    public static IJobRun NewJobRun(this IJob job, Guid? sourceId) {
      var session = EntityHelper.GetSession(job);
      var jobRun = session.NewEntity<IJobRun>();
      jobRun.Job = job;
      jobRun.SourceId = sourceId;
      jobRun.CurrentArguments = job.Arguments;
      jobRun.Status = JobRunStatus.Executing;
      jobRun.RemainingRetries = job.RetryCount;
      jobRun.RemainingRounds = job.RetryRoundsCount;
      jobRun.NextStartOn = session.Context.App.TimeService.UtcNow;
      // We use update query to append messages (Log = job.Log + message) - see JobContext class.
      // It does not work if initial value is null; so we initialize it to empty string 
      jobRun.Log = string.Empty;
      return jobRun;
    }

    internal static JobStartInfo GetJobStartInfo(LambdaExpression expression) {
      var callExpr = expression.Body as MethodCallExpression;
      Util.Check(callExpr != null, "Invalid Job definition expression - must be a method call: {0}", expression);
      var job = new JobStartInfo(); 
      job.Method = callExpr.Method;
      job.DeclaringType = job.Method.DeclaringType;
      // parameters
      job.Arguments = new object[callExpr.Arguments.Count];
      for(int i = 0; i < callExpr.Arguments.Count; i++) {
        var argExpr = callExpr.Arguments[i];
        if(argExpr.Type == typeof(JobRunContext))
          continue;
        job.Arguments[i] = ExpressionHelper.Evaluate(argExpr);
      }
      return job; 
    }

    internal static string SerializeArguments(object[] arguments, JsonSerializer serializer) {
      var serArgs = new string[arguments.Length];
      for(int i = 0; i < arguments.Length; i++) {
        var arg = arguments[i];
        if(arg == null || arg is JobRunContext)
          continue;
        serArgs[i] = Serialize(serializer, arg);
      }
      var result = string.Join(ArgsDelimiter, serArgs);
      return result;
    }

    public static JobStartInfo GetJobStartInfo(IJobRun jobRun, JobRunContext jobContext) {
      var app = jobContext.App; 
      var jobData = new JobStartInfo();
      var job = jobRun.Job; 
      jobData.DeclaringType = ReflectionHelper.GetLoadedType(job.TargetType, throwIfNotFound: true); //it will throw if cannot find it
      var methName = job.TargetMethod;
      var argCount = job.TargetParameterCount;
      jobData.Method = ReflectionHelper.FindMethod(jobData.DeclaringType, job.TargetMethod, job.TargetParameterCount); 
      // if Method is not static, it must be Module instance - this is a convention
      if(!jobData.Method.IsStatic)
        jobData.Object = app.GetGlobalObject(jobData.DeclaringType); //throws if not found. 
      //arguments
      var prms = jobData.Method.GetParameters();
      if (prms.Length == 0) {
        jobData.Arguments = new object[0];
        return jobData; 
      }
      var serArgs = jobRun.CurrentArguments;
      Util.Check(!string.IsNullOrEmpty(serArgs),
        "Serialized parameter values not found in job entity, expected {0} values.", prms.Length);
      var arrStrArgs = serArgs.Split(new[] { ArgsDelimiter }, StringSplitOptions.None);
      Util.Check(prms.Length == arrStrArgs.Length, "Serialized arg count ({0}) in job entity " +
           "does not match the number of target method parameters ({1}); target method: {2}.", 
           arrStrArgs.Length, prms.Length, jobData.Method.Name);
      // Deserialize arguments
      jobData.Arguments = new object[arrStrArgs.Length]; 
      for(int i = 0; i < arrStrArgs.Length; i++) {
        var paramType = prms[i].ParameterType;
        if(paramType == typeof(JobRunContext))
          jobData.Arguments[i] = jobContext;
        else 
          jobData.Arguments[i] = Deserialize(jobContext.Serializer, paramType, arrStrArgs[i]);
      }
      return jobData; 
    }

    private static string Serialize(JsonSerializer serializer, object value) {
      using(var stream = new MemoryStream()) {
        using(JsonTextWriter jsonTextWriter = new JsonTextWriter(new StreamWriter(stream)) { CloseOutput = false }) {
          serializer.Serialize(jsonTextWriter, value);
          jsonTextWriter.Flush();
        }
        stream.Position = 0;
        var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        return json;
      }
    }//method

    private static object Deserialize(JsonSerializer serializer, Type type, string value) {
      if(string.IsNullOrEmpty(value))
        return ReflectionHelper.GetDefaultValue(type); 
      var textReader = new StringReader(value);
      using(JsonTextReader jsonTextReader = new JsonTextReader(textReader)) {
        var obj = serializer.Deserialize(jsonTextReader, type);
        return obj;
      }
    }//method


  }//class
}//ns