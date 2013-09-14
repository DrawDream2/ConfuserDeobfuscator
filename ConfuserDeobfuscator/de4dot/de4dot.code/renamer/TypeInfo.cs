﻿/*
    Copyright (C) 2011-2013 de4dot@gmail.com

    This file is part of de4dot.

    de4dot is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    de4dot is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with de4dot.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using de4dot.code.renamer.asmmodules;
using de4dot.blocks;

namespace de4dot.code.renamer {
	class TypeInfo : MemberInfo {
		public string oldNamespace;
		public string newNamespace;
		public VariableNameState variableNameState = VariableNameState.create();
		public MTypeDef type;
		MemberInfos memberInfos;

		public INameChecker NameChecker {
			get { return type.Module.ObfuscatedFile.NameChecker; }
		}

		public TypeInfo(MTypeDef typeDef, MemberInfos memberInfos)
			: base(typeDef) {
			this.type = typeDef;
			this.memberInfos = memberInfos;
			oldNamespace = typeDef.TypeDef.Namespace.String;
		}

		bool isWinFormsClass() {
			return memberInfos.isWinFormsClass(type);
		}

		public PropertyInfo prop(MPropertyDef prop) {
			return memberInfos.prop(prop);
		}

		public EventInfo evt(MEventDef evt) {
			return memberInfos.evt(evt);
		}

		public FieldInfo field(MFieldDef field) {
			return memberInfos.field(field);
		}

		public MethodInfo method(MMethodDef method) {
			return memberInfos.method(method);
		}

		public GenericParamInfo gparam(MGenericParamDef gparam) {
			return memberInfos.gparam(gparam);
		}

		public ParamInfo param(MParamDef param) {
			return memberInfos.param(param);
		}

		TypeInfo getBase() {
			if (type.baseType == null)
				return null;

			TypeInfo baseInfo;
			memberInfos.tryGetType(type.baseType.typeDef, out baseInfo);
			return baseInfo;
		}

		bool isModuleType() {
			return type.TypeDef.IsGlobalModuleType;
		}

		public void prepareRenameTypes(TypeRenamerState state) {
			var checker = NameChecker;

			if (newNamespace == null && oldNamespace != "") {
				if (type.TypeDef.IsNested)
					newNamespace = "";
				else if (!checker.isValidNamespaceName(oldNamespace))
					newNamespace = state.createNamespace(this.type.TypeDef, oldNamespace);
			}

			string origClassName = null;
			if (isWinFormsClass())
				origClassName = findWindowsFormsClassName(type);
			if (isModuleType()) {
				if (oldNamespace != "")
					newNamespace = "";
				rename("<Module>");
			}
			else if (!checker.isValidTypeName(oldName)) {
				if (origClassName != null && checker.isValidTypeName(origClassName))
					rename(state.getTypeName(oldName, origClassName));
				else {
					ITypeNameCreator nameCreator = type.isGlobalType() ?
											state.globalTypeNameCreator :
											state.internalTypeNameCreator;
					string newBaseType = null;
					TypeInfo baseInfo = getBase();
					if (baseInfo != null && baseInfo.renamed)
						newBaseType = baseInfo.newName;
					rename(nameCreator.create(type.TypeDef, newBaseType));
				}
			}

			prepareRenameGenericParams(type.GenericParams, checker);
		}

		public void mergeState() {
			foreach (var ifaceInfo in type.interfaces)
				mergeState(ifaceInfo.typeDef);
			if (type.baseType != null)
				mergeState(type.baseType.typeDef);
		}

		void mergeState(MTypeDef other) {
			if (other == null)
				return;
			TypeInfo otherInfo;
			if (!memberInfos.tryGetType(other, out otherInfo))
				return;
			variableNameState.merge(otherInfo.variableNameState);
		}

		public void prepareRenameMembers() {
			mergeState();

			foreach (var fieldDef in type.AllFields)
				variableNameState.addFieldName(field(fieldDef).oldName);
			foreach (var eventDef in type.AllEvents)
				variableNameState.addEventName(evt(eventDef).oldName);
			foreach (var propDef in type.AllProperties)
				variableNameState.addPropertyName(prop(propDef).oldName);
			foreach (var methodDef in type.AllMethods)
				variableNameState.addMethodName(method(methodDef).oldName);

			if (isWinFormsClass())
				initializeWindowsFormsFieldsAndProps();

			prepareRenameFields();
		}

		public void prepareRenamePropsAndEvents() {
			mergeState();
			prepareRenameProperties();
			prepareRenameEvents();
		}

		void prepareRenameFields() {
			var checker = NameChecker;

			if (type.TypeDef.IsEnum) {
				var instanceFields = getInstanceFields();
				if (instanceFields.Count == 1)
					field(instanceFields[0]).rename("value__");

				int i = 0;
				string nameFormat = hasFlagsAttribute() ? "flag_{0}" : "const_{0}";
				foreach (var fieldDef in type.AllFieldsSorted) {
					var fieldInfo = field(fieldDef);
					if (fieldInfo.renamed)
						continue;
					if (!fieldDef.FieldDef.IsStatic || !fieldDef.FieldDef.IsLiteral)
						continue;
					if (!checker.isValidFieldName(fieldInfo.oldName))
						fieldInfo.rename(string.Format(nameFormat, i));
					i++;
				}
			}
			foreach (var fieldDef in type.AllFieldsSorted) {
				var fieldInfo = field(fieldDef);
				if (fieldInfo.renamed)
					continue;
				if (!checker.isValidFieldName(fieldInfo.oldName))
					fieldInfo.rename(fieldInfo.suggestedName ?? variableNameState.getNewFieldName(fieldDef.FieldDef));
			}
		}

		List<MFieldDef> getInstanceFields() {
			var fields = new List<MFieldDef>();
			foreach (var fieldDef in type.AllFields) {
				if (!fieldDef.FieldDef.IsStatic)
					fields.Add(fieldDef);
			}
			return fields;
		}

		bool hasFlagsAttribute() {
			foreach (var attr in type.TypeDef.CustomAttributes) {
				if (attr.AttributeType.FullName == "System.FlagsAttribute")
					return true;
			}
			return false;
		}

		void prepareRenameProperties() {
			foreach (var propDef in type.AllPropertiesSorted) {
				if (propDef.isVirtual())
					continue;
				prepareRenameProperty(propDef);
			}
		}

		void prepareRenameProperty(MPropertyDef propDef) {
			if (propDef.isVirtual())
				throw new ApplicationException("Can't rename virtual props here");
			var propInfo = prop(propDef);
			if (propInfo.renamed)
				return;

			string propName = propInfo.oldName;
			if (!NameChecker.isValidPropertyName(propName))
				propName = propInfo.suggestedName;
			if (!NameChecker.isValidPropertyName(propName)) {
				if (propDef.isItemProperty())
					propName = "Item";
				else
					propName = variableNameState.getNewPropertyName(propDef.PropertyDef);
			}
			variableNameState.addPropertyName(propName);
			propInfo.rename(propName);

			renameSpecialMethod(propDef.GetMethod, "get_" + propName);
			renameSpecialMethod(propDef.SetMethod, "set_" + propName);
		}

		void prepareRenameEvents() {
			foreach (var eventDef in type.AllEventsSorted) {
				if (eventDef.isVirtual())
					continue;
				prepareRenameEvent(eventDef);
			}
		}

		void prepareRenameEvent(MEventDef eventDef) {
			if (eventDef.isVirtual())
				throw new ApplicationException("Can't rename virtual events here");
			var eventInfo = evt(eventDef);
			if (eventInfo.renamed)
				return;

			string eventName = eventInfo.oldName;
			if (!NameChecker.isValidEventName(eventName))
				eventName = eventInfo.suggestedName;
			if (!NameChecker.isValidEventName(eventName))
				eventName = variableNameState.getNewEventName(eventDef.EventDef);
			variableNameState.addEventName(eventName);
			eventInfo.rename(eventName);

			renameSpecialMethod(eventDef.AddMethod, "add_" + eventName);
			renameSpecialMethod(eventDef.RemoveMethod, "remove_" + eventName);
			renameSpecialMethod(eventDef.RaiseMethod, "raise_" + eventName);
		}

		void renameSpecialMethod(MMethodDef methodDef, string newName) {
			if (methodDef == null)
				return;
			if (methodDef.isVirtual())
				return;
			renameMethod(methodDef, newName);
		}

		public void prepareRenameMethods() {
			mergeState();
			foreach (var methodDef in type.AllMethodsSorted) {
				if (methodDef.isVirtual())
					continue;
				renameMethod(methodDef);
			}
		}

		public void prepareRenameMethods2() {
			var checker = NameChecker;
			foreach (var methodDef in type.AllMethodsSorted) {
				prepareRenameMethodArgs(methodDef);
				prepareRenameGenericParams(methodDef.GenericParams, checker, methodDef.Owner == null ? null : methodDef.Owner.GenericParams);
			}
		}

		void prepareRenameMethodArgs(MMethodDef methodDef) {
			VariableNameState newVariableNameState = null;
			ParamInfo info;
			if (methodDef.VisibleParameterCount > 0) {
				if (isEventHandler(methodDef)) {
					info = param(methodDef.ParamDefs[methodDef.VisibleParameterBaseIndex]);
					if (!info.gotNewName())
						info.newName = "sender";

					info = param(methodDef.ParamDefs[methodDef.VisibleParameterBaseIndex + 1]);
					if (!info.gotNewName())
						info.newName = "e";
				}
				else {
					newVariableNameState = variableNameState.cloneParamsOnly();
					var checker = NameChecker;
					foreach (var paramDef in methodDef.ParamDefs) {
						if (paramDef.IsHiddenThisParameter)
							continue;
						info = param(paramDef);
						if (info.gotNewName())
							continue;
						if (!checker.isValidMethodArgName(info.oldName))
							info.newName = newVariableNameState.getNewParamName(info.oldName, paramDef.ParameterDef);
					}
				}
			}

			info = param(methodDef.ReturnParamDef);
			if (!info.gotNewName()) {
				if (!NameChecker.isValidMethodReturnArgName(info.oldName)) {
					if (newVariableNameState == null)
						newVariableNameState = variableNameState.cloneParamsOnly();
					info.newName = newVariableNameState.getNewParamName(info.oldName, methodDef.ReturnParamDef.ParameterDef);
				}
			}

			if ((methodDef.Property != null && methodDef == methodDef.Property.SetMethod) ||
				(methodDef.Event != null && (methodDef == methodDef.Event.AddMethod || methodDef == methodDef.Event.RemoveMethod))) {
				if (methodDef.VisibleParameterCount > 0) {
					var paramDef = methodDef.ParamDefs[methodDef.ParamDefs.Count - 1];
					param(paramDef).newName = "value";
				}
			}
		}

		bool canRenameMethod(MMethodDef methodDef) {
			var methodInfo = method(methodDef);
			if (methodDef.isStatic()) {
				if (methodInfo.oldName == ".cctor")
					return false;
			}
			else if (methodDef.isVirtual()) {
				if (DotNetUtils.derivesFromDelegate(type.TypeDef)) {
					switch (methodInfo.oldName) {
					case "BeginInvoke":
					case "EndInvoke":
					case "Invoke":
						return false;
					}
				}
			}
			else {
				if (methodInfo.oldName == ".ctor")
					return false;
			}
			return true;
		}

		public void renameMethod(MMethodDef methodDef, string methodName) {
			if (!canRenameMethod(methodDef))
				return;
			var methodInfo = method(methodDef);
			variableNameState.addMethodName(methodName);
			methodInfo.rename(methodName);
		}

		void renameMethod(MMethodDef methodDef) {
			if (methodDef.isVirtual())
				throw new ApplicationException("Can't rename virtual methods here");
			if (!canRenameMethod(methodDef))
				return;

			var info = method(methodDef);
			if (info.renamed)
				return;
			info.renamed = true;
			var checker = NameChecker;

			// PInvoke methods' EntryPoint is always valid. It has to, so always rename.
			bool isValidName = NameChecker.isValidMethodName(info.oldName);
			bool isExternPInvoke = methodDef.MethodDef.ImplMap != null && methodDef.MethodDef.RVA == 0;
			if (!isValidName || isExternPInvoke) {
				INameCreator nameCreator = null;
				string newName = info.suggestedName;
				string newName2;
				if (methodDef.MethodDef.ImplMap != null && !string.IsNullOrEmpty(newName2 = getPinvokeName(methodDef)))
					newName = newName2;
				else if (methodDef.isStatic())
					nameCreator = variableNameState.staticMethodNameCreator;
				else
					nameCreator = variableNameState.instanceMethodNameCreator;
				if (!string.IsNullOrEmpty(newName))
					nameCreator = new NameCreator2(newName);
				renameMethod(methodDef, variableNameState.getNewMethodName(info.oldName, nameCreator));
			}
		}

		string getPinvokeName(MMethodDef methodDef) {
			var entryPoint = methodDef.MethodDef.ImplMap.Name.String;
			if (Regex.IsMatch(entryPoint, @"^#\d+$"))
				entryPoint = DotNetUtils.getDllName(methodDef.MethodDef.ImplMap.Module.Name.String) + "_" + entryPoint.Substring(1);
			return entryPoint;
		}

		static bool isEventHandler(MMethodDef methodDef) {
			var sig = methodDef.MethodDef.MethodSig;
			if (sig == null || sig.Params.Count != 2)
				return false;
			if (sig.RetType.ElementType != ElementType.Void)
				return false;
			if (sig.Params[0].ElementType != ElementType.Object)
				return false;
			if (!sig.Params[1].FullName.Contains("EventArgs"))
				return false;
			return true;
		}

		void prepareRenameGenericParams(IEnumerable<MGenericParamDef> genericParams, INameChecker checker) {
			prepareRenameGenericParams(genericParams, checker, null);
		}

		void prepareRenameGenericParams(IEnumerable<MGenericParamDef> genericParams, INameChecker checker, IEnumerable<MGenericParamDef> otherGenericParams) {
			var usedNames = new Dictionary<string, bool>(StringComparer.Ordinal);
			var nameCreator = new GenericParamNameCreator();

			if (otherGenericParams != null) {
				foreach (var param in otherGenericParams) {
					var gpInfo = memberInfos.gparam(param);
					usedNames[gpInfo.newName] = true;
				}
			}

			foreach (var param in genericParams) {
				var gpInfo = memberInfos.gparam(param);
				if (!checker.isValidGenericParamName(gpInfo.oldName) || usedNames.ContainsKey(gpInfo.oldName)) {
					string newName;
					do {
						newName = nameCreator.create();
					} while (usedNames.ContainsKey(newName));
					usedNames[newName] = true;
					gpInfo.rename(newName);
				}
			}
		}

		void initializeWindowsFormsFieldsAndProps() {
			var checker = NameChecker;

			var ourFields = new FieldDefAndDeclaringTypeDict<MFieldDef>();
			foreach (var fieldDef in type.AllFields)
				ourFields.add(fieldDef.FieldDef, fieldDef);
			var ourMethods = new MethodDefAndDeclaringTypeDict<MMethodDef>();
			foreach (var methodDef in type.AllMethods)
				ourMethods.add(methodDef.MethodDef, methodDef);

			foreach (var methodDef in type.AllMethods) {
				if (methodDef.MethodDef.Body == null)
					continue;
				if (methodDef.MethodDef.IsStatic || methodDef.MethodDef.IsVirtual)
					continue;
				var instructions = methodDef.MethodDef.Body.Instructions;
				for (int i = 2; i < instructions.Count; i++) {
					var call = instructions[i];
					if (call.OpCode.Code != Code.Call && call.OpCode.Code != Code.Callvirt)
						continue;
					if (!isWindowsFormsSetNameMethod(call.Operand as IMethod))
						continue;

					var ldstr = instructions[i - 1];
					if (ldstr.OpCode.Code != Code.Ldstr)
						continue;
					var fieldName = ldstr.Operand as string;
					if (fieldName == null || !checker.isValidFieldName(fieldName))
						continue;

					var instr = instructions[i - 2];
					IField fieldRef = null;
					if (instr.OpCode.Code == Code.Call || instr.OpCode.Code == Code.Callvirt) {
						var calledMethod = instr.Operand as IMethod;
						if (calledMethod == null)
							continue;
						var calledMethodDef = ourMethods.find(calledMethod);
						if (calledMethodDef == null)
							continue;
						fieldRef = getFieldRef(calledMethodDef.MethodDef);

						var propDef = calledMethodDef.Property;
						if (propDef == null)
							continue;

						memberInfos.prop(propDef).suggestedName = fieldName;
						fieldName = "_" + fieldName;
					}
					else if (instr.OpCode.Code == Code.Ldfld) {
						fieldRef = instr.Operand as IField;
					}

					if (fieldRef == null)
						continue;
					var fieldDef = ourFields.find(fieldRef);
					if (fieldDef == null)
						continue;
					var fieldInfo = memberInfos.field(fieldDef);

					if (fieldInfo.renamed)
						continue;

					fieldInfo.suggestedName = variableNameState.getNewFieldName(fieldInfo.oldName, new NameCreator2(fieldName));
				}
			}
		}

		static IField getFieldRef(MethodDef method) {
			if (method == null || method.Body == null)
				return null;
			var instructions = method.Body.Instructions;
			int index = 0;
			var ldarg0 = DotNetUtils.getInstruction(instructions, ref index);
			if (ldarg0 == null || ldarg0.GetParameterIndex() != 0)
				return null;
			var ldfld = DotNetUtils.getInstruction(instructions, ref index);
			if (ldfld == null || ldfld.OpCode.Code != Code.Ldfld)
				return null;
			var ret = DotNetUtils.getInstruction(instructions, ref index);
			if (ret == null)
				return null;
			if (ret.IsStloc()) {
				var local = ret.GetLocal(method.Body.Variables);
				ret = DotNetUtils.getInstruction(instructions, ref index);
				if (ret == null || !ret.IsLdloc())
					return null;
				if (ret.GetLocal(method.Body.Variables) != local)
					return null;
				ret = DotNetUtils.getInstruction(instructions, ref index);
			}
			if (ret == null || ret.OpCode.Code != Code.Ret)
				return null;
			return ldfld.Operand as IField;
		}

		public void initializeEventHandlerNames() {
			var ourFields = new FieldDefAndDeclaringTypeDict<MFieldDef>();
			foreach (var fieldDef in type.AllFields)
				ourFields.add(fieldDef.FieldDef, fieldDef);
			var ourMethods = new MethodDefAndDeclaringTypeDict<MMethodDef>();
			foreach (var methodDef in type.AllMethods)
				ourMethods.add(methodDef.MethodDef, methodDef);

			initVbEventHandlers(ourFields, ourMethods);
			initFieldEventHandlers(ourFields, ourMethods);
			initTypeEventHandlers(ourFields, ourMethods);
		}

		// VB initializes the handlers in the property setter, where it first removes the handler
		// from the previous control, and then adds the handler to the new control.
		void initVbEventHandlers(FieldDefAndDeclaringTypeDict<MFieldDef> ourFields, MethodDefAndDeclaringTypeDict<MMethodDef> ourMethods) {
			var checker = NameChecker;

			foreach (var propDef in type.AllProperties) {
				var setterDef = propDef.SetMethod;
				if (setterDef == null)
					continue;

				string eventName;
				var handler = getVbHandler(setterDef.MethodDef, out eventName);
				if (handler == null)
					continue;
				var handlerDef = ourMethods.find(handler);
				if (handlerDef == null)
					continue;

				if (!checker.isValidEventName(eventName))
					continue;

				memberInfos.method(handlerDef).suggestedName = string.Format("{0}_{1}", memberInfos.prop(propDef).newName, eventName);
			}
		}

		static IMethod getVbHandler(MethodDef method, out string eventName) {
			eventName = null;
			if (method.Body == null)
				return null;
			var sig = method.MethodSig;
			if (sig == null)
				return null;
			if (sig.RetType.ElementType != ElementType.Void)
				return null;
			if (sig.Params.Count != 1)
				return null;
			if (method.Body.Variables.Count != 1)
				return null;
			if (!isEventHandlerType(method.Body.Variables[0].Type))
				return null;

			var instructions = method.Body.Instructions;
			int index = 0;

			int newobjIndex = findInstruction(instructions, index, Code.Newobj);
			if (newobjIndex == -1 || findInstruction(instructions, newobjIndex + 1, Code.Newobj) != -1)
				return null;
			if (!isEventHandlerCtor(instructions[newobjIndex].Operand as IMethod))
				return null;
			if (newobjIndex < 1)
				return null;
			var ldvirtftn = instructions[newobjIndex - 1];
			if (ldvirtftn.OpCode.Code != Code.Ldvirtftn && ldvirtftn.OpCode.Code != Code.Ldftn)
				return null;
			var handlerMethod = ldvirtftn.Operand as IMethod;
			if (handlerMethod == null)
				return null;
			if (!new SigComparer().Equals(method.DeclaringType, handlerMethod.DeclaringType))
				return null;
			index = newobjIndex;

			IField addField, removeField;
			IMethod addMethod, removeMethod;
			if (!findEventCall(instructions, ref index, out removeField, out removeMethod))
				return null;
			if (!findEventCall(instructions, ref index, out addField, out addMethod))
				return null;

			if (findInstruction(instructions, index, Code.Callvirt) != -1)
				return null;
			if (!new SigComparer().Equals(addField, removeField))
				return null;
			if (!new SigComparer().Equals(method.DeclaringType, addField.DeclaringType))
				return null;
			if (!new SigComparer().Equals(addMethod.DeclaringType, removeMethod.DeclaringType))
				return null;
			if (!Utils.StartsWith(addMethod.Name.String, "add_", StringComparison.Ordinal))
				return null;
			if (!Utils.StartsWith(removeMethod.Name.String, "remove_", StringComparison.Ordinal))
				return null;
			eventName = addMethod.Name.String.Substring(4);
			if (eventName != removeMethod.Name.String.Substring(7))
				return null;
			if (eventName == "")
				return null;

			return handlerMethod;
		}

		static bool findEventCall(IList<Instruction> instructions, ref int index, out IField field, out IMethod calledMethod) {
			field = null;
			calledMethod = null;

			int callvirt = findInstruction(instructions, index, Code.Callvirt);
			if (callvirt < 2)
				return false;
			index = callvirt + 1;

			var ldloc = instructions[callvirt - 1];
			if (ldloc.OpCode.Code != Code.Ldloc_0)
				return false;

			var ldfld = instructions[callvirt - 2];
			if (ldfld.OpCode.Code != Code.Ldfld)
				return false;

			field = ldfld.Operand as IField;
			calledMethod = instructions[callvirt].Operand as IMethod;
			return field != null && calledMethod != null;
		}

		static int findInstruction(IList<Instruction> instructions, int index, Code code) {
			for (int i = index; i < instructions.Count; i++) {
				if (instructions[i].OpCode.Code == code)
					return i;
			}
			return -1;
		}

		void initFieldEventHandlers(FieldDefAndDeclaringTypeDict<MFieldDef> ourFields, MethodDefAndDeclaringTypeDict<MMethodDef> ourMethods) {
			var checker = NameChecker;

			foreach (var methodDef in type.AllMethods) {
				if (methodDef.MethodDef.Body == null)
					continue;
				if (methodDef.MethodDef.IsStatic)
					continue;
				var instructions = methodDef.MethodDef.Body.Instructions;
				for (int i = 0; i < instructions.Count - 6; i++) {
					// We're looking for this code pattern:
					//	ldarg.0
					//	ldfld field
					//	ldarg.0
					//	ldftn method / ldarg.0 + ldvirtftn
					//	newobj event_handler_ctor
					//	callvirt add_SomeEvent

					if (instructions[i].GetParameterIndex() != 0)
						continue;
					int index = i + 1;

					var ldfld = instructions[index++];
					if (ldfld.OpCode.Code != Code.Ldfld)
						continue;
					var fieldRef = ldfld.Operand as IField;
					if (fieldRef == null)
						continue;
					var fieldDef = ourFields.find(fieldRef);
					if (fieldDef == null)
						continue;

					if (instructions[index++].GetParameterIndex() != 0)
						continue;

					IMethod methodRef;
					var instr = instructions[index + 1];
					if (instr.OpCode.Code == Code.Ldvirtftn) {
						if (!isThisOrDup(instructions[index++]))
							continue;
						var ldvirtftn = instructions[index++];
						methodRef = ldvirtftn.Operand as IMethod;
					}
					else {
						var ldftn = instructions[index++];
						if (ldftn.OpCode.Code != Code.Ldftn)
							continue;
						methodRef = ldftn.Operand as IMethod;
					}
					if (methodRef == null)
						continue;
					var handlerMethod = ourMethods.find(methodRef);
					if (handlerMethod == null)
						continue;

					var newobj = instructions[index++];
					if (newobj.OpCode.Code != Code.Newobj)
						continue;
					if (!isEventHandlerCtor(newobj.Operand as IMethod))
						continue;

					var call = instructions[index++];
					if (call.OpCode.Code != Code.Call && call.OpCode.Code != Code.Callvirt)
						continue;
					var addHandler = call.Operand as IMethod;
					if (addHandler == null)
						continue;
					if (!Utils.StartsWith(addHandler.Name.String, "add_", StringComparison.Ordinal))
						continue;

					var eventName = addHandler.Name.String.Substring(4);
					if (!checker.isValidEventName(eventName))
						continue;

					memberInfos.method(handlerMethod).suggestedName = string.Format("{0}_{1}", memberInfos.field(fieldDef).newName, eventName);
				}
			}
		}

		void initTypeEventHandlers(FieldDefAndDeclaringTypeDict<MFieldDef> ourFields, MethodDefAndDeclaringTypeDict<MMethodDef> ourMethods) {
			var checker = NameChecker;

			foreach (var methodDef in type.AllMethods) {
				if (methodDef.MethodDef.Body == null)
					continue;
				if (methodDef.MethodDef.IsStatic)
					continue;
				var method = methodDef.MethodDef;
				var instructions = method.Body.Instructions;
				for (int i = 0; i < instructions.Count - 5; i++) {
					// ldarg.0
					// ldarg.0 / dup
					// ldarg.0 / dup
					// ldvirtftn handler
					// newobj event handler ctor
					// call add_Xyz

					if (instructions[i].GetParameterIndex() != 0)
						continue;
					int index = i + 1;

					if (!isThisOrDup(instructions[index++]))
						continue;
					IMethod handler;
					if (instructions[index].OpCode.Code == Code.Ldftn) {
						handler = instructions[index++].Operand as IMethod;
					}
					else {
						if (!isThisOrDup(instructions[index++]))
							continue;
						var instr = instructions[index++];
						if (instr.OpCode.Code != Code.Ldvirtftn)
							continue;
						handler = instr.Operand as IMethod;
					}
					if (handler == null)
						continue;
					var handlerDef = ourMethods.find(handler);
					if (handlerDef == null)
						continue;

					var newobj = instructions[index++];
					if (newobj.OpCode.Code != Code.Newobj)
						continue;
					if (!isEventHandlerCtor(newobj.Operand as IMethod))
						continue;

					var call = instructions[index++];
					if (call.OpCode.Code != Code.Call && call.OpCode.Code != Code.Callvirt)
						continue;
					var addMethod = call.Operand as IMethod;
					if (addMethod == null)
						continue;
					if (!Utils.StartsWith(addMethod.Name.String, "add_", StringComparison.Ordinal))
						continue;

					var eventName = addMethod.Name.String.Substring(4);
					if (!checker.isValidEventName(eventName))
						continue;

					memberInfos.method(handlerDef).suggestedName = string.Format("{0}_{1}", newName, eventName);
				}
			}
		}

		static bool isThisOrDup(Instruction instr) {
			return instr.GetParameterIndex() == 0 || instr.OpCode.Code == Code.Dup;
		}

		static bool isEventHandlerCtor(IMethod method) {
			if (method == null)
				return false;
			if (method.Name != ".ctor")
				return false;
			if (!DotNetUtils.isMethod(method, "System.Void", "(System.Object,System.IntPtr)"))
				return false;
			if (!isEventHandlerType(method.DeclaringType))
				return false;
			return true;
		}

		static bool isEventHandlerType(IType type) {
			return type.FullName.EndsWith("EventHandler", StringComparison.Ordinal);
		}

		string findWindowsFormsClassName(MTypeDef type) {
			foreach (var methodDef in type.AllMethods) {
				if (methodDef.MethodDef.Body == null)
					continue;
				if (methodDef.MethodDef.IsStatic || methodDef.MethodDef.IsVirtual)
					continue;
				var instructions = methodDef.MethodDef.Body.Instructions;
				for (int i = 2; i < instructions.Count; i++) {
					var call = instructions[i];
					if (call.OpCode.Code != Code.Call && call.OpCode.Code != Code.Callvirt)
						continue;
					if (!isWindowsFormsSetNameMethod(call.Operand as IMethod))
						continue;

					var ldstr = instructions[i - 1];
					if (ldstr.OpCode.Code != Code.Ldstr)
						continue;
					var className = ldstr.Operand as string;
					if (className == null)
						continue;

					if (instructions[i - 2].GetParameterIndex() != 0)
						continue;

					findInitializeComponentMethod(type, methodDef);
					return className;
				}
			}
			return null;
		}

		void findInitializeComponentMethod(MTypeDef type, MMethodDef possibleInitMethod) {
			foreach (var methodDef in type.AllMethods) {
				if (methodDef.MethodDef.Name != ".ctor")
					continue;
				if (methodDef.MethodDef.Body == null)
					continue;
				foreach (var instr in methodDef.MethodDef.Body.Instructions) {
					if (instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt)
						continue;
					if (!MethodEqualityComparer.CompareDeclaringTypes.Equals(possibleInitMethod.MethodDef, instr.Operand as IMethod))
						continue;

					memberInfos.method(possibleInitMethod).suggestedName = "InitializeComponent";
					return;
				}
			}
		}

		static bool isWindowsFormsSetNameMethod(IMethod method) {
			if (method == null)
				return false;
			if (method.Name.String != "set_Name")
				return false;
			var sig = method.MethodSig;
			if (sig == null)
				return false;
			if (sig.RetType.ElementType != ElementType.Void)
				return false;
			if (sig.Params.Count != 1)
				return false;
			if (sig.Params[0].ElementType != ElementType.String)
				return false;
			return true;
		}
	}
}