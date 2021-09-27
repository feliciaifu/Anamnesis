﻿// © Anamnesis.
// Licensed under the MIT license.

namespace Anamnesis
{
	using System;
	using System.Collections.Generic;
	using System.ComponentModel;
	using System.Reflection;
	using System.Runtime.InteropServices;
	using System.Text;
	using Anamnesis.Memory;
	using PropertyChanged;
	using Serilog;

	public delegate void ViewModelEvent(object sender);

	[AddINotifyPropertyChangedInterface]
	public abstract class StructViewModelBase : IStructViewModel, INotifyPropertyChanged
	{
		protected object model;
		private readonly Dictionary<string, BindInfo> binds = new Dictionary<string, BindInfo>();
		private bool suppressViewToModelEvents = false;

		public StructViewModelBase()
		{
			Type modelType = this.GetModelType();
			PropertyInfo[]? properties = this.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);

			foreach (PropertyInfo property in properties)
			{
				string name = property.Name;

				ModelFieldAttribute? attribute = property.GetCustomAttribute<ModelFieldAttribute>();
				if (attribute == null)
					continue;

				string fieldName = attribute.FieldName ?? property.Name;

				FieldInfo? modelField = modelType.GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
				if (modelField == null)
				{
					Log.Error($"No field for property: {name} in view model: {this.GetType()}");
					continue;
				}

				BindInfo bind = new BindInfo(property, modelField);

				this.binds.Add(name, bind);
			}

			this.PropertyChanged += this.OnThisPropertyChanged;
			this.Enabled = true;

			object? model = Activator.CreateInstance(this.GetModelType());

			if (model == null)
				throw new Exception($"Failed to create instance of model type: {this.GetModelType()}");

			this.model = model;
		}

		public StructViewModelBase(IStructViewModel? parent)
			: this()
		{
			this.Parent = parent;
		}

		public StructViewModelBase(IStructViewModel parent, string propertyName)
			: this(parent)
		{
			PropertyInfo? property = parent.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);

			if (property == null)
				throw new Exception($"Unable to find property: {propertyName} on object: {this.Parent}");

			this.ParentProperty = property;
		}

		/// <summary>
		/// Called when the view is updated from the backing model. (FFXIV -> Anamnesis)
		/// </summary>
		public event ViewModelEvent? ModelChanged;

		/// <summary>
		/// Called when the model is updated from the view model. (Anamnesis -> FFXIV)
		/// </summary>
		public event ViewModelEvent? ViewModelChanged;

		/// <summary>
		/// Called when a property within the view model is changed. (from FFXIV or Anamnesis)
		/// </summary>
		public event PropertyChangedEventHandler? PropertyChanged;

		/// <summary>
		/// Gets or sets a value indicating whether updating the view model is allowed.
		/// </summary>
		public bool Enabled { get; set; }

		public IStructViewModel? Parent { get; protected set; }
		public PropertyInfo? ParentProperty { get; }

		public virtual string Path
		{
			get
			{
				StringBuilder builder = new StringBuilder();
				builder.Append(this.GetType().Name);
				IStructViewModel? vm = this.Parent;
				while (vm != null)
				{
					builder.Append("<--");
					builder.Append(vm.GetType().Name);

					vm = vm.Parent;
				}

				return builder.ToString();
			}
		}

		protected static ILogger Log => Serilog.Log.ForContext<StructViewModelBase>();

		public abstract Type GetModelType();

		public virtual void Import(object model)
		{
			// Import properties that are not view models first
			foreach (BindInfo bind in this.binds.Values)
			{
				if (bind.IsViewModel)
					continue;

				bind.ViewModelProperty.SetValue(this, bind.ModelField.GetValue(model));
			}

			// Import properties that are view models last
			foreach (BindInfo bind in this.binds.Values)
			{
				if (!bind.IsViewModel)
					continue;

				bind.ViewModelProperty.SetValue(this, bind.ModelField.GetValue(model));
			}
		}

		public virtual void SetModel(object? model)
		{
			if (!MemoryService.IsProcessAlive)
				return;

			if (model == null)
				throw new Exception("Attempt to set null model to view model");

			this.model = model;

			bool changed = false;
			this.suppressViewToModelEvents = true;

			// Update properties that are not view models first
			foreach (BindInfo bind in this.binds.Values)
			{
				if (bind.IsViewModel)
					continue;

				changed |= this.HandleModelToViewUpdate(bind.ViewModelProperty, bind.ModelField);
			}

			// Update properties that are view models last
			foreach (BindInfo bind in this.binds.Values)
			{
				if (!bind.IsViewModel)
					continue;

				changed |= this.HandleModelToViewUpdate(bind.ViewModelProperty, bind.ModelField);
			}

			this.suppressViewToModelEvents = false;

			if (changed)
			{
				this.ModelChanged?.Invoke(this);
			}
		}

		public object? GetModel()
		{
			return this.model;
		}

		public virtual void ReadChanges()
		{
			if (!this.Enabled)
				return;

			if (this.Parent != null && this.ParentProperty != null)
			{
				object? obj = this.ParentProperty.GetValue(this.Parent);
				this.SetModel(obj);
			}

			throw new Exception("View model is not correctly initialized");
		}

		public TParent? GetParent<TParent>()
			where TParent : IStructViewModel
		{
			if (this is TParent t)
				return t;

			if (this.Parent == null)
				return default;

			return this.Parent.GetParent<TParent>();
		}

		public int GetOffset(string propertyName)
		{
			PropertyInfo? property = this.GetType().GetProperty(propertyName);
			if (property == null)
				throw new Exception($"view model: {this.GetType()} does not contain property: {propertyName}");

			return this.GetOffset(property);
		}

		public int GetOffset(PropertyInfo viewModelProperty)
		{
			ModelFieldAttribute? modelFielAttribute = viewModelProperty.GetCustomAttribute<ModelFieldAttribute>();

			if (modelFielAttribute == null)
				throw new Exception("Attempt to get offset for property that is not a model field binding");

			Type modelType = this.GetModelType();
			FieldInfo? modelField = modelType.GetField(viewModelProperty.Name);

			if (modelField == null)
				throw new Exception($"Model: {modelType} does not have field: {viewModelProperty.Name}");

			return this.GetOffset(modelField);
		}

		public int GetOffset(FieldInfo modelField)
		{
			FieldOffsetAttribute? offsetAttribute = modelField.GetCustomAttribute<FieldOffsetAttribute>();

			if (offsetAttribute == null)
				throw new NotImplementedException($"Attempt to get offset for model: {this.GetModelType()} field: {modelField.Name} that does not have an explicit offset. This is not supported.");

			return offsetAttribute.Value;
		}

		void IStructViewModel.RaisePropertyChanged(string propertyName)
		{
			this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		}

		protected virtual bool HandleModelToViewUpdate(PropertyInfo viewModelProperty, FieldInfo modelField)
		{
			lock (this)
			{
				object? lhs = viewModelProperty.GetValue(this);
				object? rhs = modelField.GetValue(this.model);

				if (typeof(IStructViewModel).IsAssignableFrom(viewModelProperty.PropertyType))
				{
					IStructViewModel? vm = null;

					if (lhs != null)
						vm = (IStructViewModel)lhs;

					if (vm == null)
						vm = Activator.CreateInstance(viewModelProperty.PropertyType, this, viewModelProperty.Name) as IStructViewModel;

					if (vm == null)
						throw new Exception($"Failed to create instance of view model: {viewModelProperty.PropertyType}");

					vm.SetModel(rhs);
					rhs = vm;
				}
				else
				{
					if (modelField.FieldType != viewModelProperty.PropertyType)
						throw new Exception($"view model: {this.GetType()} property: {modelField.Name} type: {viewModelProperty.PropertyType} does not match backing model field type: {modelField.FieldType}");

					if (lhs == null && rhs == null)
					{
						return false;
					}
				}

				if (rhs == null || !rhs.Equals(lhs))
				{
					viewModelProperty.SetValue(this, rhs);
					this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(viewModelProperty.Name));
					this.OnModelToView(modelField.Name, rhs);
					return true;
				}
			}

			return false;
		}

		protected virtual bool HandleViewToModelUpdate(PropertyInfo viewModelProperty, FieldInfo modelField)
		{
			object? lhs = viewModelProperty.GetValue(this);
			object? rhs = modelField.GetValue(this.model);

			if (lhs is IStructViewModel vm)
				lhs = vm.GetModel();

			if (lhs == null && rhs == null)
				return false;

			if (lhs == null)
				return false;

			if (rhs == null || !rhs.Equals(lhs))
			{
				TypedReference typedReference = __makeref(this.model);
				modelField.SetValueDirect(typedReference, lhs);

				this.OnViewToModel(viewModelProperty.Name, lhs);
				return true;
			}

			return false;
		}

		/// <summary>
		/// Called when the view model has changed the backing modal.
		/// </summary>
		protected virtual void OnViewToModel(string fieldName, object? value)
		{
			if (this.Parent != null && this.ParentProperty != null)
			{
				if (typeof(IStructViewModel).IsAssignableFrom(this.ParentProperty.PropertyType))
				{
					this.Parent.RaisePropertyChanged(this.ParentProperty.Name);
				}
				else
				{
					this.ParentProperty.SetValue(this.Parent, this.model);
				}
			}
		}

		/// <summary>
		/// Called when the backing model has changed the view model.
		/// </summary>
		protected virtual void OnModelToView(string fieldName, object? value)
		{
		}

		private void OnThisPropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (!this.Enabled || this.suppressViewToModelEvents)
				return;

			if (e.PropertyName == null)
				return;

			if (!this.binds.ContainsKey(e.PropertyName))
				return;

			BindInfo bind = this.binds[e.PropertyName];
			bool changed = this.HandleViewToModelUpdate(bind.ViewModelProperty, bind.ModelField);

			if (changed)
			{
				this.ViewModelChanged?.Invoke(this);
			}
		}

		private class BindInfo
		{
			public readonly PropertyInfo ViewModelProperty;
			public readonly FieldInfo ModelField;
			public readonly bool IsViewModel;

			public BindInfo(PropertyInfo viewModelProperty, FieldInfo modelField)
			{
				this.ViewModelProperty = viewModelProperty;
				this.ModelField = modelField;
				this.IsViewModel = typeof(IStructViewModel).IsAssignableFrom(viewModelProperty.PropertyType);
			}
		}
	}
}